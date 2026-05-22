
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Geometry;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.Wpf.SharpDX;
using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using UrToolClient.Models;

namespace UrToolClient.ViewModels
{
    public partial class SimulationViewModel : ObservableObject
    {
        // HelixToolkit SharpDX 必须提供 EffectsManager，否则 Viewport 黑屏
        public IEffectsManager EffectsManager { get; } = new DefaultEffectsManager();

        public ObservableElement3DCollection SceneModels { get; } = new();

        public ObservableCollection<RobotJoint> Joints { get; } = new();

        public SimulationViewModel()
        {
            AddGroundPlane();
        }
        [RelayCommand]
        private async Task LoadRobotAsync()
        {
            try
            {
                // 1. 切回主线程清空 UI 绑定的集合，并重建地面
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SceneModels.Clear();
                    Joints.Clear();
                    AddGroundPlane();
                });

                // 2. 解析本地路径
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string urdfPath = Path.Combine(baseDir, "ur", "urdf", "ur10e.urdf");
                string packageRoot = Path.Combine(baseDir, "ur");

                if (!File.Exists(urdfPath))
                {
                    MessageBox.Show($"找不到URDF文件: {urdfPath}\n请在VS里选中ur文件夹下的文件，将属性设为'如果较新则复制'。", "提示");
                    return;
                }

                XDocument urdfDoc = XDocument.Load(urdfPath);
                var importer = new Importer();

                // 3. 加载根连杆（base 连杆，不属于任何旋转关节的子连杆，需单独加载）
                // 只在旋转关节的子连杆集内查找，避免 fixed 关节干扰判断
                var revoluteChildLinks = new System.Collections.Generic.HashSet<string?>(
                    urdfDoc.Descendants("joint")
                           .Where(j => j.Attribute("type")?.Value == "revolute")
                           .Select(j => j.Element("child")?.Attribute("link")?.Value));

                string? rootLinkName = urdfDoc.Descendants("joint")
                    .Where(j => j.Attribute("type")?.Value == "revolute")
                    .Select(j => j.Element("parent")?.Attribute("link")?.Value)
                    .FirstOrDefault(name => name != null && !revoluteChildLinks.Contains(name));

                if (rootLinkName != null)
                {
                    var rootLinkEl = urdfDoc.Descendants("link").FirstOrDefault(l => l.Attribute("name")?.Value == rootLinkName);
                    string? rootRosPath = rootLinkEl?.Element("visual")?.Element("geometry")?.Element("mesh")?.Attribute("filename")?.Value;
                    if (!string.IsNullOrEmpty(rootRosPath))
                    {
                        string relPath = rootRosPath.Replace("package://", "").Replace("/", "\\");
                        int mi = relPath.IndexOf("meshes");
                        if (mi >= 0) relPath = relPath.Substring(mi);
                        string localRootPath = Path.Combine(packageRoot, relPath);
                        if (File.Exists(localRootPath))
                        {
                            var rootScene = await Task.Run(() => importer.Load(localRootPath));
                            if (rootScene?.Root != null)
                            {
                                var rootModel = new SceneNodeGroupModel3D();

                                // 沿 fixed-joint 链向上累积坐标系变换（与 UpdateKinematics 中旋转关节的处理方式保持一致）
                                Matrix3D fixedChainMat = Matrix3D.Identity;
                                string walkLink = rootLinkName;
                                while (true)
                                {
                                    var parentFixedJoint = urdfDoc.Descendants("joint")
                                        .FirstOrDefault(j => j.Attribute("type")?.Value == "fixed"
                                                          && j.Element("child")?.Attribute("link")?.Value == walkLink);
                                    if (parentFixedJoint == null) break;

                                    var pjOrigin = parentFixedJoint.Element("origin");
                                    double[] pjXyz = ParseStringArray(pjOrigin?.Attribute("xyz")?.Value, 3, new double[3]);
                                    double[] pjRpy = ParseStringArray(pjOrigin?.Attribute("rpy")?.Value, 3, new double[3]);
                                    Matrix3D pjMat = RpyToMatrix(pjRpy[0], pjRpy[1], pjRpy[2]);
                                    pjMat.OffsetX = pjXyz[0]; pjMat.OffsetY = pjXyz[1]; pjMat.OffsetZ = pjXyz[2];
                                    fixedChainMat = pjMat * fixedChainMat;

                                    walkLink = parentFixedJoint.Element("parent")?.Attribute("link")?.Value ?? "";
                                    if (string.IsNullOrEmpty(walkLink)) break;
                                }

                                // 视觉偏移（含 Assimp Y-Up 补偿）* fixed 链资款
                                var vOriginEl = rootLinkEl?.Element("visual")?.Element("origin");
                                double[] rvRpy = ParseStringArray(vOriginEl?.Attribute("rpy")?.Value, 3, new double[3]);
                                double[] rvXyz = ParseStringArray(vOriginEl?.Attribute("xyz")?.Value, 3, new double[3]);
                                Matrix3D visualMat = RpyToMatrix(rvRpy[0] + Math.PI / 2, rvRpy[1], rvRpy[2]);
                                visualMat.OffsetX = rvXyz[0]; visualMat.OffsetY = rvXyz[1]; visualMat.OffsetZ = rvXyz[2];
                                Matrix3D rootMat = visualMat * fixedChainMat;

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    rootModel.AddNode(rootScene.Root);
                                    rootModel.Transform = new MatrixTransform3D(rootMat);
                                    SceneModels.Add(rootModel);
                                });
                            }
                        }
                    }
                }

                // 4. 筛选出所有旋转关节
                var jointElements = urdfDoc.Descendants("joint").Where(j => j.Attribute("type")?.Value == "revolute").ToList();

                foreach (var jointEl in jointElements)
                {
                    var joint = new RobotJoint
                    {
                        JointName = jointEl.Attribute("name")?.Value ?? "未知关节",
                        ChildLinkName = jointEl.Element("child")?.Attribute("link")?.Value ?? ""
                    };

                    // ---- A. 解析关节物理连杆信息 (<joint> 下的 origin 和 axis) ----
                    double[] axisParams = ParseStringArray(jointEl.Element("axis")?.Attribute("xyz")?.Value, 3, new double[] { 0, 0, 1 });
                    joint.Axis = new Vector3D(axisParams[0], axisParams[1], axisParams[2]);

                    var originEl = jointEl.Element("origin");
                    double[] xyz = ParseStringArray(originEl?.Attribute("xyz")?.Value, 3, new double[3]);
                    double[] rpy = ParseStringArray(originEl?.Attribute("rpy")?.Value, 3, new double[3]);

                    joint.OriginXYZ = new Vector3D(xyz[0], xyz[1], xyz[2]);
                    joint.OriginRPY = new Vector3D(rpy[0], rpy[1], rpy[2]);

                    // ---- B. 解析 3D 网格的视觉补偿信息 (<link> 下的 visual/origin) ----
                    var linkEl = urdfDoc.Descendants("link").FirstOrDefault(l => l.Attribute("name")?.Value == joint.ChildLinkName);
                    string? rosPath = linkEl?.Element("visual")?.Element("geometry")?.Element("mesh")?.Attribute("filename")?.Value;

                    var visualOriginEl = linkEl?.Element("visual")?.Element("origin");
                    double[] vXyz = ParseStringArray(visualOriginEl?.Attribute("xyz")?.Value, 3, new double[3]);
                    double[] vRpy = ParseStringArray(visualOriginEl?.Attribute("rpy")?.Value, 3, new double[3]);

                    joint.VisualXYZ = new Vector3D(vXyz[0], vXyz[1], vXyz[2]);

                    // 🌟 核心补偿：给 Roll（X轴旋转）加上 90 度 (Math.PI / 2) 
                    // 用于抵消 Assimp 导入 .dae 时自动加上的 -90 度 Y-Up 转换，让模型变回标准的 Z-Up
                    joint.VisualRPY = new Vector3D(vRpy[0] + (Math.PI / 2), vRpy[1], vRpy[2]);

                    // ---- C. 异步加载 3D 模型 ----
                    if (!string.IsNullOrEmpty(rosPath))
                    {
                        // 智能截取路径
                        string relativePath = rosPath.Replace("package://", "").Replace("/", "\\");
                        int meshesIndex = relativePath.IndexOf("meshes");
                        if (meshesIndex >= 0) relativePath = relativePath.Substring(meshesIndex);

                        string localPath = Path.Combine(packageRoot, relativePath);

                        if (File.Exists(localPath))
                        {
                            // 在后台线程加载，防止卡死 UI
                            var scene = await Task.Run(() => importer.Load(localPath));
                            if (scene != null && scene.Root != null)
                            {
                                // 切回主线程更新 UI 集合
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    joint.LinkModel.AddNode(scene.Root);
                                    SceneModels.Add(joint.LinkModel);
                                });
                            }
                        }
                    }

                    // 监听 UI 滑块事件
                    joint.PropertyChanged += OnJointPropertyChanged;

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Joints.Add(joint);
                    });
                }

                // 5. 初始组装
                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateKinematics();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载模型失败: {ex.Message}");
            }
        }

        private void UpdateKinematics()
        {
            Transform3D currentGlobalTransform = new MatrixTransform3D(Matrix3D.Identity);

            foreach (var joint in Joints)
            {
                Transform3DGroup localTransform = new Transform3DGroup();

                // 1. 动态旋转 (UI 绑定的关节角度)
                localTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(joint.Axis, joint.AngleDegree)));

                // 2. 静态旋转 (关节层级 Origin RPY)
                // 这里调用你自定义的 RpyToMatrix 方法
                Matrix3D jointRpyMat = RpyToMatrix(joint.OriginRPY.X, joint.OriginRPY.Y, joint.OriginRPY.Z);
                localTransform.Children.Add(new MatrixTransform3D(jointRpyMat));

                // 3. 静态平移 (关节层级 Origin XYZ)
                localTransform.Children.Add(new TranslateTransform3D(joint.OriginXYZ.X, joint.OriginXYZ.Y, joint.OriginXYZ.Z));

                // 4. 计算出该关节的全局物理坐标系
                currentGlobalTransform = new MatrixTransform3D(localTransform.Value * currentGlobalTransform.Value);

                // ==========================================
                // 5. 🌟 追加网格自身的视觉偏移 (Visual XYZ / RPY)
                // ==========================================
                Matrix3D visualMat = RpyToMatrix(joint.VisualRPY.X, joint.VisualRPY.Y, joint.VisualRPY.Z);
                visualMat.OffsetX = joint.VisualXYZ.X;
                visualMat.OffsetY = joint.VisualXYZ.Y;
                visualMat.OffsetZ = joint.VisualXYZ.Z;

                // 最终 3D 模型的矩阵 = 视觉自身偏移矩阵 * 关节的全局物理坐标系矩阵
                Matrix3D finalMeshMatrix = visualMat * currentGlobalTransform.Value;

                // 6. 应用最终变换到 Helix Toolkit 模型上
                joint.LinkModel.Transform = new MatrixTransform3D(finalMeshMatrix);
            }
        }

        /// <summary>
        /// 按 URDF 约定（固定轴 XYZ，等效于 Rz*Ry*Rx）构造旋转矩阵。
        /// WPF Matrix3D 行优先：M = Rx * Ry * Rz 等效于点变换 p' = p * Rx * Ry * Rz = p * (Rz*Ry*Rx)^T，
        /// 因此这里直接用 Matrix3D.Append 顺序 Rx→Ry→Rz 即可得到正确的 URDF rpy 旋转。
        /// </summary>
        private static Matrix3D RpyToMatrix(double roll, double pitch, double yaw)
        {
            var mx = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), roll * 180.0 / Math.PI));
            var my = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), pitch * 180.0 / Math.PI));
            var mz = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), yaw * 180.0 / Math.PI));
            Matrix3D m = mx.Value;
            m.Append(my.Value);
            m.Append(mz.Value);
            return m;
        }

        /// <summary>
        /// 将机器人实时关节角（弧度）同步到仿真模型。
        /// 由 MainViewModel 的轮询循环在 UI 线程调用。
        /// </summary>
        public void ApplyJointAngles(double[] radAngles)
        {
            if (Joints.Count == 0 || radAngles.Length < 6) return;

            static double ToDeg(double r) => r * 180.0 / Math.PI;

            // 暂时解除事件监听，批量赋值后统一刷新一次运动学，避免多次重绘
            for (int i = 0; i < Math.Min(Joints.Count, 6); i++)
            {
                Joints[i].PropertyChanged -= OnJointPropertyChanged;
                Joints[i].AngleDegree = ToDeg(radAngles[i]);
                Joints[i].PropertyChanged += OnJointPropertyChanged;
            }

            UpdateKinematics();
        }

        private void OnJointPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RobotJoint.AngleDegree)) UpdateKinematics();
        }

        /// <summary>
        /// 创建地面平面（Z=0），机械臂基座固定于中心点。
        /// </summary>
        private void AddGroundPlane()
        {
            const float size = 3.0f;
            const int divisions = 20;
            float step = size / divisions;
            float half = size / 2f;

            // 主平面
            var builder = new MeshBuilder();
            builder.AddQuad(
                new Vector3(-half, -half, 0),
                new Vector3(half, -half, 0),
                new Vector3(half, half, 0),
                new Vector3(-half, half, 0));

            SceneModels.Add(new MeshGeometryModel3D
            {
                Geometry = builder.ToMeshGeometry3D(),
                Material = PhongMaterials.Gray,
                IsTransparent = true,
            });

            // 网格线（用多个小矩形拼接网格外观）
            var lineColor = System.Windows.Media.Color.FromArgb(140, 150, 150, 160);
            var lineBuilder = new MeshBuilder();
            float lineHalf = 0.002f; // 线宽一半（m）
            for (int i = 0; i <= divisions; i++)
            {
                float pos = -half + i * step;
                // 平行 X 轴的细长矩形
                lineBuilder.AddQuad(
                    new Vector3(-half, pos - lineHalf, 0.001f),
                    new Vector3(half, pos - lineHalf, 0.001f),
                    new Vector3(half, pos + lineHalf, 0.001f),
                    new Vector3(-half, pos + lineHalf, 0.001f));
                // 平行 Y 轴的细长矩形
                lineBuilder.AddQuad(
                    new Vector3(pos - lineHalf, -half, 0.001f),
                    new Vector3(pos + lineHalf, -half, 0.001f),
                    new Vector3(pos + lineHalf, half, 0.001f),
                    new Vector3(pos - lineHalf, half, 0.001f));
            }
            SceneModels.Add(new MeshGeometryModel3D
            {
                Geometry = lineBuilder.ToMeshGeometry3D(),
                Material = PhongMaterials.LightGray,
                IsTransparent = true,
            });
        }

        private double[] ParseStringArray(string? input, int expectedLength, double[] defaultValues)
        {
            if (string.IsNullOrWhiteSpace(input)) return defaultValues;
            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != expectedLength) return defaultValues;
            return parts.Select(p => double.TryParse(p, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val) ? val : 0).ToArray();
        }
    }
}
