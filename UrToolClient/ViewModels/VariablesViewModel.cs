using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using UrToolClient.Helper;
using UrToolClient.Models;

namespace UrToolClient.ViewModels;

public partial class VariablesViewModel : ObservableObject
{
    private readonly ILogger<VariablesViewModel> _logger;
    public ObservableCollection<UrVariableItem> Variables { get; } = new();

    // 保存原始xml 的时间戳，导出时原样写回，避免UR控制器识别错误
    [ObservableProperty]
    private string _timestamp;

    private readonly string _filePath = "default.variables";

    public VariablesViewModel(ILogger<VariablesViewModel> logger)
    {
        _logger = logger;
    }

    [RelayCommand]
    private async Task LoadVariables()
    {
        _logger.LogInformation("正在加载变量配置...");
        if (!File.Exists(_filePath))
        {
            MessageBox.Show($"未找到配置文件: {Path.GetFullPath(_filePath)}，将创建新配置。", "提示");
            _logger.LogWarning("未找到配置文件: {FilePath}，将创建新配置。", Path.GetFullPath(_filePath));
            return;
        }

        try
        {
            var result = await URHelper.OpenSetUpConfigAsync(_filePath);
            if (result.Root != null)
            {
                // 备份时间戳
                Timestamp = result.Root.Attribute("timestamp")?.Value ?? throw new ParseFailException("未找到时间戳属性。");
                Variables.Clear();
                foreach (var el in result.Root.Elements("variable"))
                {
                    Variables.Add(new UrVariableItem
                    {
                        Name = el.Attribute("name")?.Value ?? throw new ParseFailException("变量缺少name属性。"),
                        Value = el.Attribute("value")?.Value ?? throw new ParseFailException("变量缺少value属性。"),
                        IsFavourite = el.Attribute("isFavourite")?.Value.ToLower() == "true",
                        Description = el.Attribute("description")?.Value ?? string.Empty,
                        IsNameEditable = false, // 从文件加载的变量默认不允许修改名称，避免误操作导致与UR控制器不匹配
                    });
                }
            }
            _logger.LogInformation("变量配置加载完成，成功解析 {Count} 个变量。", Variables.Count);
        }
        catch (ParseFailException ex)
        {
            Variables.Clear();
            MessageBox.Show($"解析配置文件失败: {ex.Message}", "错误");
            _logger.LogError(ex, "解析配置文件失败: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            Variables.Clear();
            MessageBox.Show($"加载配置文件时发生错误: {ex.Message}", "错误");
            _logger.LogError(ex, "解析配置文件失败: {Message}", ex.Message);
        }

    }

    /// <summary>
    /// 新增一行变量
    /// </summary>
    [RelayCommand]
    private void AddVariable()
    {
        Variables.Add(new UrVariableItem
        {
            Name = "new_var_ui",
            Value = "[0,0,0,0,0,0]",
            IsFavourite = false,
            Description = "",
            IsNameEditable = true, // 新增的变量默认允许修改名称
        });
    }

    /// <summary>
    /// 删除选中的变量行
    /// </summary>
    /// <param name="item"></param>
    [RelayCommand]
    private void DeleteVariable(UrVariableItem item)
    {
        if (item != null)
        {
            Variables.Remove(item);
        }
    }

    /// <summary>
    /// 组合当前内存数据，写回符合 UR 标准的XML文件
    /// </summary>
    [RelayCommand]
    private async Task SaveVariables()
    {
        try
        {
            // 构建标准的根节点与时间戳
            XElement root = new XElement("InstallationVariables", new XAttribute("timestamp", Timestamp));

            // 序列化所有变量行
            foreach (var variable in Variables)
            {
                XElement varElement = new XElement("variable",
                    new XAttribute("name", variable.Name),
                    new XAttribute("value", variable.Value),
                    new XAttribute("isFavourite", variable.IsFavourite.ToString().ToLower()),
                    new XAttribute("description", variable.Description ?? string.Empty)
                );
                root.Add(varElement);
            }
            XDocument doc = new XDocument(root);

            await URHelper.SaveCompressedXmlWithoutHeaderAsync(doc, _filePath);
            MessageBox.Show("安装变量已成功保存并导出！", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }




}
internal class ParseFailException : Exception
{
    public ParseFailException() { }

    public ParseFailException(string message) : base(message) { }

    public ParseFailException(string message, Exception inner) : base(message, inner) { }
}