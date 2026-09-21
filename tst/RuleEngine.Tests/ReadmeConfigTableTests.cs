using Templates.Data;

namespace ServiceTests;

/// <summary>Keeps the README config table honest: config is the public API of a container.</summary>
public class ReadmeConfigTableTests
{
    [Fact]
    public void Every_option_is_documented_in_the_readme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var readme = File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));

        var sections = new (string Section, Type Type)[]
        {
            (TemplatesOptions.SectionName, typeof(TemplatesOptions)),
            (TemplateApiOptions.SectionName, typeof(TemplateApiOptions)),
            (TemplateHistoryOptions.SectionName, typeof(TemplateHistoryOptions)),
            (TemplateVersionCacheOptions.SectionName, typeof(TemplateVersionCacheOptions)),
        };
        var missing = sections
            .SelectMany(s => s.Type.GetProperties().Where(p => p.SetMethod is not null).Select(p => $"{s.Section}__{p.Name}"))
            .Append("ConnectionStrings__Redis").Append("ENVIRONMENT")
            .Where(env => !readme.Contains($"`{env}`"))
            .ToList();

        Assert.True(missing.Count == 0, $"README config table is missing: {string.Join(", ", missing)}");
    }
}
