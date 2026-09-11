using System.Xml.Linq;

namespace CycleArc.Tests;

public class DpiManifestTests
{
    [Fact]
    public void WpfExecutable_DeclaresPerMonitorDpiBeforeWindowCreation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CycleArc.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root!.FullName, "src", "CycleArc");
        var project = XDocument.Load(Path.Combine(directory, "CycleArc.csproj"));
        Assert.Equal("app.manifest", project.Descendants("ApplicationManifest").Single().Value);
        var manifest = XDocument.Load(Path.Combine(directory, "app.manifest"));
        XNamespace modern = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
        XNamespace legacy = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";
        Assert.Equal("PerMonitorV2", manifest.Descendants(modern + "dpiAwareness").Single().Value);
        Assert.Equal("true/pm", manifest.Descendants(legacy + "dpiAware").Single().Value);
    }
}
