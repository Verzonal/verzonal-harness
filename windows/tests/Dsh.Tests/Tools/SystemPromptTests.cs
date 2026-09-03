using Dsh.Cordis;
using Dsh.Llm;
using Dsh.SystemPrompt;
using Dsh.Tools;

namespace Dsh.Tests.Tools;

public sealed class SystemPromptTests
{
    private static async Task<(Context Ctx, SystemPromptService Prompt)> ServiceAsync(bool identity = false)
    {
        var ctx = Context.CreateRoot();
        var fiber = ctx.Plugin(SystemPromptService.Plugin(identity));
        await fiber.WhenSettledAsync();
        return (ctx, ctx.Require<SystemPromptService>(SystemPromptKeys.Service));
    }

    [Fact]
    public async Task Sections_render_in_ascending_order_separated_by_blank_lines()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.Section(ctx, PromptSection.Fixed("second", 10, "world"));
        prompt.Section(ctx, PromptSection.Fixed("first", -10, "hello"));

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Equal("hello\n\nworld", SystemPromptService.RenderPrompt(assembly));
    }

    [Fact]
    public async Task An_empty_section_contributes_nothing_to_the_rendered_prompt()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.Section(ctx, PromptSection.Fixed("filled", 0, "text"));
        prompt.Section(ctx, PromptSection.Fixed("blank", 1, string.Empty));

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Equal("text", SystemPromptService.RenderPrompt(assembly));
    }

    [Fact]
    public async Task The_built_in_identity_section_is_included_when_the_deployment_asks_for_it()
    {
        var (_, prompt) = await ServiceAsync(identity: true);

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Contains("DeepSeek Harness", SystemPromptService.RenderPrompt(assembly), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_complete_section_replaces_every_other()
    {
        var (ctx, prompt) = await ServiceAsync(identity: true);
        prompt.Section(ctx, PromptSection.Fixed("ordinary", 5, "should not appear"));
        prompt.Section(ctx, PromptSection.Fixed("whole", 0, "this is the whole prompt", complete: true));

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Equal("this is the whole prompt", SystemPromptService.RenderPrompt(assembly));
    }

    [Fact]
    public async Task A_listener_cannot_add_text_beside_a_complete_section()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.Section(ctx, PromptSection.Fixed("whole", 0, "only this", complete: true));
        ctx.OnWaterfall(SystemPromptKeys.Assemble, async (payload, next) =>
        {
            var assembly = await next();
            return assembly with
            {
                Sections = [.. assembly.Sections, new AssembledSection("smuggled", "extra text")],
            };
        });

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Equal("only this", SystemPromptService.RenderPrompt(assembly));
    }

    [Fact]
    public async Task Two_complete_sections_are_a_loud_misconfiguration()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.Section(ctx, PromptSection.Fixed("one", 0, "a", complete: true));
        prompt.Section(ctx, PromptSection.Fixed("two", 1, "b", complete: true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => prompt.AssembleAsync(new AssembleContext()));
    }

    [Fact]
    public async Task A_scoped_section_shadows_a_global_one_with_the_same_name()
    {
        var (ctx, prompt) = await ServiceAsync();
        var scope = new ScopeKey("agent");
        prompt.Section(ctx, PromptSection.Fixed(SystemPromptService.PersonaSection, 0, "generic persona"));
        prompt.Section(ctx.WithScope(scope), PromptSection.Fixed(SystemPromptService.PersonaSection, 0, "specialised persona"));

        var global = await prompt.AssembleAsync(new AssembleContext());
        var scoped = await prompt.AssembleAsync(new AssembleContext(scope));

        Assert.Equal("generic persona", SystemPromptService.RenderPrompt(global));
        Assert.Equal("specialised persona", SystemPromptService.RenderPrompt(scoped));
    }

    [Fact]
    public async Task Variables_interpolate_into_sections()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.Variable(ctx, "workspace", _ => "/home/project");
        prompt.Section(ctx, PromptSection.Fixed("cwd", 0, "You are working in {{workspace}}."));

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Equal("You are working in /home/project.", SystemPromptService.RenderPrompt(assembly));
    }

    [Fact]
    public async Task An_unregistered_variable_fails_loudly_rather_than_shipping_a_literal_placeholder()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.Section(ctx, PromptSection.Fixed("cwd", 0, "You are in {{nowhere}}."));

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        var error = Assert.Throws<InvalidOperationException>(() => SystemPromptService.RenderPrompt(assembly));
        Assert.Contains("unregistered variable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_opening_brace_pair_is_ordinary_prose()
    {
        var text = SystemPromptService.Interpolate(
            "write {{ like this in your answer",
            new Dictionary<string, string>(StringComparer.Ordinal),
            "slot");

        Assert.Equal("write {{ like this in your answer", text);
    }

    [Fact]
    public void A_substituted_value_is_not_rescanned_for_placeholders()
    {
        var text = SystemPromptService.Interpolate(
            "{{outer}}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["outer"] = "{{inner}}" },
            "slot");

        Assert.Equal("{{inner}}", text);
    }

    [Fact]
    public async Task An_illegal_variable_name_is_refused_at_registration()
    {
        var (ctx, prompt) = await ServiceAsync();

        Assert.Throws<InvalidOperationException>(() => prompt.Variable(ctx, "Not-Legal", _ => "x"));
    }

    [Fact]
    public async Task Runtime_context_sections_render_under_a_superseding_preamble()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.ContextSection(ctx, new PromptContextSection("files", 0, _ => "Open file: a.txt"));

        var assembly = await prompt.AssembleAsync(new AssembleContext());
        var joined = SystemPromptService.JoinContextSections(SystemPromptService.RenderContextSections(assembly));

        Assert.Contains("supersedes earlier runtime-context snapshots", joined, StringComparison.Ordinal);
        Assert.Contains("Open file: a.txt", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suppressing_runtime_context_removes_it_even_if_a_listener_puts_it_back()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.ContextSection(ctx, new PromptContextSection("files", 0, _ => "Open file: a.txt"));
        prompt.SuppressRuntimeContext(ctx);
        ctx.OnWaterfall(SystemPromptKeys.Assemble, async (payload, next) =>
        {
            var assembly = await next();
            return assembly with { Contexts = [new AssembledSection("smuggled", "context")] };
        });

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Empty(assembly.Contexts);
    }

    [Fact]
    public async Task Tool_schemas_arrive_in_a_stable_order_regardless_of_provider_order()
    {
        var (ctx, prompt) = await ServiceAsync();
        prompt.ToolProvider(ctx, _ => new ToolProviderResult(
        [
            new ToolSchema("zebra", "z", new Dictionary<string, object?>(StringComparer.Ordinal)),
            new ToolSchema("alpha", "a", new Dictionary<string, object?>(StringComparer.Ordinal)),
        ]));
        prompt.ToolProvider(ctx, _ => new ToolProviderResult(
        [
            new ToolSchema("mid", "m", new Dictionary<string, object?>(StringComparer.Ordinal)),
        ]));

        var assembly = await prompt.AssembleAsync(new AssembleContext());

        Assert.Equal(["alpha", "mid", "zebra"], assembly.Tools.Select(static schema => schema.Name));
    }

    [Fact]
    public async Task A_tool_registry_can_feed_the_assembly_through_a_provider()
    {
        var ctx = Context.CreateRoot();
        await ctx.Plugin(ToolRuntime.Plugin()).WhenSettledAsync();
        await ctx.Plugin(SystemPromptService.Plugin(false)).WhenSettledAsync();
        var tools = ctx.Require<ToolRuntime>(ToolKeys.Service);
        var prompt = ctx.Require<SystemPromptService>(SystemPromptKeys.Service);
        prompt.ToolProvider(ctx, assemble => new ToolProviderResult(tools.Schemas(assemble.Scope)));

        var before = await prompt.AssembleAsync(new AssembleContext());
        Assert.Empty(before.Tools);

        tools.Register(ctx, new RegistryTool());
        var after = await prompt.AssembleAsync(new AssembleContext());

        Assert.Equal(["noop"], after.Tools.Select(static schema => schema.Name));
    }

    private sealed class RegistryTool : ToolBase
    {
        public override string Name => "noop";

        public override string Description => "Does nothing.";

        public override JsonSchemaNode Parameters { get; } = Schema.EmptyObject();

        public override ToolOutput Output { get; } = new(Schema.Any(), (_, _) => []);

        public override Task<Dsh.Session.JsonValue> ExecuteAsync(Dsh.Session.JsonValue args, ToolRunContext exec)
            => Task.FromResult(Dsh.Session.JsonValue.Null);
    }
}
