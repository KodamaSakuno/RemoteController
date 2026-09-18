namespace RemoteController.Server.Tests;

public class WebClientPageTests
{
    // 内嵌 web 控制端随二进制分发，资源缺失须在构建/测试期暴露而非运行期 404
    [Fact]
    public void EmbeddedWebClientPageExists()
    {
        using var stream = typeof(Program).Assembly
            .GetManifestResourceStream("RemoteController.Server.WebClient.index.html");

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Contains("<canvas", reader.ReadToEnd());
    }
}
