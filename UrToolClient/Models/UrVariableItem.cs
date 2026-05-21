using CommunityToolkit.Mvvm.ComponentModel;

namespace UrToolClient.Models
{
    public partial class UrVariableItem : ObservableObject
    {
        [ObservableProperty]
        private string _name;
        [ObservableProperty]
        private string _value;
        [ObservableProperty]
        private string _description;
        [ObservableProperty]
        private bool _isFavourite;

        // 新增：标记名称是否可编辑
        [ObservableProperty]
        private bool _isNameEditable = false;
    }
}
