using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Kashira.Editor.ViewModels;

namespace Kashira.Editor;

/// <summary>뷰모델 → 뷰 해석(…ViewModels.XxxViewModel → …Views.XxxView).</summary>
[RequiresUnreferencedCode("ViewLocator 는 리플렉션을 사용한다.")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);
        return type != null ? (Control)Activator.CreateInstance(type)! : new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
