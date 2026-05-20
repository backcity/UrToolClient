using System.Windows.Controls;
using UrToolClient.ViewModels;

namespace UrToolClient.Views
{
    public partial class CalibrationPage : UserControl
    {
        public CalibrationPage(CalibrationViewModel vm)
        {
            DataContext = vm;
            InitializeComponent();
        }
    }
}
