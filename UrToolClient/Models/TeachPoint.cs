using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace UrToolClient.Models
{
    /// <summary>
    /// 单个示教点位，存储原始弧度值，对外暴露角度显示属性。
    /// </summary>
    public partial class TeachPoint : ObservableObject
    {
        public int Index { get; set; }

        [ObservableProperty] private string _name;

        // 原始关节角（弧度），长度固定为 6
        public double[] JointRad { get; }

        public DateTime RecordedAt { get; } = DateTime.Now;

        // ── 角度显示属性（只读，供 UI 绑定）──────────────────
        public double J1Deg => ToDeg(JointRad[0]);
        public double J2Deg => ToDeg(JointRad[1]);
        public double J3Deg => ToDeg(JointRad[2]);
        public double J4Deg => ToDeg(JointRad[3]);
        public double J5Deg => ToDeg(JointRad[4]);
        public double J6Deg => ToDeg(JointRad[5]);

        public TeachPoint(int index, string name, double[] jointRad)
        {
            Index    = index;
            _name    = name;
            JointRad = (double[])jointRad.Clone(); // 防止外部缓冲区被覆盖
        }

        /// <summary>生成 URScript 关节数组字符串（弧度）</summary>
        public string ToUrScriptRad()
            => $"[{JointRad[0]:F4},{JointRad[1]:F4},{JointRad[2]:F4},"
             + $"{JointRad[3]:F4},{JointRad[4]:F4},{JointRad[5]:F4}]";

        /// <summary>生成角度字符串（度）</summary>
        public string ToDegreesString()
            => $"[{J1Deg:F2}°,{J2Deg:F2}°,{J3Deg:F2}°,"
             + $"{J4Deg:F2}°,{J5Deg:F2}°,{J6Deg:F2}°]";

        private static double ToDeg(double r) => r * 180.0 / Math.PI;
    }
}
