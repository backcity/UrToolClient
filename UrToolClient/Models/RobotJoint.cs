using CommunityToolkit.Mvvm.ComponentModel;
using HelixToolkit.Wpf.SharpDX;
using System.Windows.Media.Media3D;

namespace UrToolClient.Models
{
    public partial class RobotJoint : ObservableObject
    {
        public string JointName { get; set; } = string.Empty;
        public string ChildLinkName { get; set; } = string.Empty;

        // URDF 关节旋转轴（在关节坐标系下）
        public Vector3D Axis { get; set; } = new Vector3D(0, 0, 1);

        // URDF joint/origin：父连杆坐标系 → 关节坐标系的变换
        public Vector3D OriginXYZ { get; set; }
        public Vector3D OriginRPY { get; set; }

        // URDF link/visual/origin：关节坐标系 → 网格本地坐标系的变换
        public Vector3D VisualXYZ { get; set; }
        public Vector3D VisualRPY { get; set; }

        // Helix Toolkit 的 3D 模型节点
        public SceneNodeGroupModel3D LinkModel { get; set; } = new SceneNodeGroupModel3D();

        // 绑定的滑块角度
        [ObservableProperty]
        private double _angleDegree;
    }
}
