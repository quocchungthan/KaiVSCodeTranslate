using GitHub.Copilot;
using Kai.Engine.Agents;
using Kai.Engine.Ingest;

namespace Kai.Engine.Tests;

public sealed class AgentDefinitionAndIngestTests
{
    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".github", "translation-pipeline.defaults.json")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Loads_the_real_repository_agents()
    {
        var agents = AgentDefinitionLoader.LoadAll(Path.Combine(FindRepositoryRoot(), ".github", "agents"));
        var huong = Assert.Single(agents, a => a.Name == "Huong");
        Assert.StartsWith("Autonomous local PDF-to-Vietnamese", huong.Description);
        Assert.StartsWith("You are Huong", huong.Prompt);
        Assert.DoesNotContain("---", huong.Prompt[..10]);
        Assert.Contains(agents, a => a.Name == "Lan");
    }

    [Theory]
    [InlineData("My Book.pdf", "My Book")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\x\\y\\Deep.PDF", "Deep")]
    [InlineData("...", "book")]
    [InlineData("a<b>c|d.pdf", "a_b_c_d")]
    public void Sanitizes_upload_file_names(string input, string expected) =>
        Assert.Equal(expected, FileIngestService.SanitizeFileName(input));

    [Fact]
    public void Unattended_user_input_never_blocks()
    {
        var freeform = CopilotAgentSessionFactory.AnswerUnattended(new UserInputRequest { Question = "Which?", AllowFreeform = true });
        Assert.True(freeform.WasFreeform);
        Assert.Contains("unattended", freeform.Answer);

        var choice = CopilotAgentSessionFactory.AnswerUnattended(new UserInputRequest { Question = "Pick", AllowFreeform = false, Choices = ["first", "second"] });
        Assert.False(choice.WasFreeform);
        Assert.Equal("first", choice.Answer);
    }
}
