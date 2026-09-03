using System.Text;
using System.Text.RegularExpressions;
using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.SystemPrompt;

/// <summary>What an assembly is being built for.</summary>
/// <param name="Scope">The registration boundary whose contributions apply.</param>
/// <param name="CancellationToken">Cancels the assembly.</param>
public sealed record AssembleContext(ScopeKey? Scope = null, CancellationToken CancellationToken = default);

/// <summary>
/// One contribution to the system prompt.
/// </summary>
/// <param name="Name">Identifies the slot, so a preset can shadow it by registering the same name.</param>
/// <param name="Order">
/// Sections are concatenated in ascending order. By convention the harness identity
/// sits at -100, the deployment persona at 0, and tool guidance in the 100s.
/// </param>
/// <param name="Text">The section's text, resolved at assembly time.</param>
/// <param name="Complete">
/// When true this contribution <em>is</em> the whole system prompt. At most one may
/// claim this, and nothing else survives beside it.
/// </param>
public sealed record PromptSection(
    string Name,
    int Order,
    Func<AssembleContext, string> Text,
    bool Complete = false)
{
    /// <summary>
    /// A section with fixed text.
    /// </summary>
    /// <param name="name">Identifies the slot.</param>
    /// <param name="order">Where it sorts.</param>
    /// <param name="text">The text.</param>
    /// <param name="complete">Whether it replaces the whole prompt.</param>
    /// <returns>The section.</returns>
    public static PromptSection Fixed(string name, int order, string text, bool complete = false)
        => new(name, order, _ => text, complete);
}

/// <summary>
/// One contribution to the runtime-context snapshot — the state the model is told
/// about each step, separate from the standing instructions in the prompt.
/// </summary>
/// <param name="Name">Identifies the slot.</param>
/// <param name="Order">Where it sorts among the other context sections.</param>
/// <param name="Text">The section's text, resolved at assembly time.</param>
public sealed record PromptContextSection(string Name, int Order, Func<AssembleContext, string> Text);

/// <summary>What a tool provider contributed.</summary>
/// <param name="Schemas">The tool schemas to offer the model.</param>
public sealed record ToolProviderResult(IReadOnlyList<ToolSchema> Schemas);

/// <summary>One resolved section, before variable interpolation.</summary>
/// <param name="Name">The slot it came from.</param>
/// <param name="Text">Its resolved text.</param>
public sealed record AssembledSection(string Name, string Text);

/// <summary>
/// Everything one request's prefix is built from.
/// </summary>
/// <param name="Sections">The system-prompt sections, in order.</param>
/// <param name="Contexts">The runtime-context sections, in order.</param>
/// <param name="Tools">The tool schemas, in canonical order.</param>
/// <param name="Variables">Values the sections may interpolate.</param>
public sealed record PromptAssembly(
    IReadOnlyList<AssembledSection> Sections,
    IReadOnlyList<AssembledSection> Contexts,
    IReadOnlyList<ToolSchema> Tools,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>Context and event keys the prompt capability publishes.</summary>
public static class SystemPromptKeys
{
    /// <summary>The context key <see cref="SystemPromptService" /> is published under.</summary>
    public const string Service = "systemPrompt";

    /// <summary>
    /// The assembled prefix, before it is rendered. A listener may reorder, rewrite,
    /// or replace it; what the chain returns is what the request carries.
    /// </summary>
    public static WaterfallKey<AssembleContext, PromptAssembly> Assemble { get; } =
        new("system-prompt/assemble");
}

/// <summary>
/// Assembles the system prompt and tool schemas for one request.
/// </summary>
/// <remarks>
/// Contributions are named slots so a preset can shadow one by registering the same
/// name, and are ordered by an explicit number so a plugin never depends on load
/// order. A section marked complete replaces the whole prompt, and that is enforced
/// after the waterfall so no listener can smuggle text in beside it.
/// </remarks>
public sealed class SystemPromptService : Service
{
    private sealed class Layer
    {
        public Dictionary<string, PromptSection> Sections { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PromptContextSection> Contexts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Func<AssembleContext, string?>> Variables { get; } = new(StringComparer.Ordinal);
        public List<Func<AssembleContext, ToolProviderResult>> ToolProviders { get; } = [];
        public int Suppressors { get; set; }
    }

    private static readonly Regex VariableName = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"\{\{([^{}]*)\}\}", RegexOptions.Compiled);

    /// <summary>The slot the harness identity occupies.</summary>
    public const string IdentitySection = "harness:identity";

    /// <summary>The slot a deployment's persona occupies.</summary>
    public const string PersonaSection = "deployment:persona";

    private readonly Layer _global = new();
    private readonly Dictionary<ScopeKey, Layer> _scoped = [];
    private readonly object _gate = new();

    /// <param name="ctx">The mounting plugin's context.</param>
    public SystemPromptService(Context ctx) : base(ctx, SystemPromptKeys.Service) { }

    private Layer LayerFor(ScopeKey? scope)
    {
        if (scope is null) return _global;
        lock (_gate)
        {
            if (!_scoped.TryGetValue(scope, out var layer))
            {
                layer = new Layer();
                _scoped[scope] = layer;
            }

            return layer;
        }
    }

    /// <summary>
    /// Contribute a system-prompt section.
    /// </summary>
    /// <param name="ctx">The registering context; its scope decides which layer the section joins.</param>
    /// <param name="section">The section.</param>
    /// <returns>A disposer that withdraws it.</returns>
    /// <exception cref="InvalidOperationException">The layer already has a section of that name.</exception>
    public IDisposable Section(Context ctx, PromptSection section)
    {
        var layer = LayerFor(ctx.Scope);
        lock (_gate)
        {
            if (layer.Sections.ContainsKey(section.Name))
            {
                throw new InvalidOperationException($"prompt section \"{section.Name}\" is already registered in this scope");
            }

            layer.Sections[section.Name] = section;
        }

        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate) layer.Sections.Remove(section.Name);
        }));
    }

    /// <summary>
    /// Contribute a runtime-context section.
    /// </summary>
    /// <param name="ctx">The registering context.</param>
    /// <param name="section">The section.</param>
    /// <returns>A disposer that withdraws it.</returns>
    public IDisposable ContextSection(Context ctx, PromptContextSection section)
    {
        var layer = LayerFor(ctx.Scope);
        lock (_gate) layer.Contexts[section.Name] = section;
        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate) layer.Contexts.Remove(section.Name);
        }));
    }

    /// <summary>
    /// Stop the runtime-context snapshot from being produced at all.
    /// </summary>
    /// <param name="ctx">The registering context.</param>
    /// <returns>A disposer that lifts the suppression.</returns>
    public IDisposable SuppressRuntimeContext(Context ctx)
    {
        var layer = LayerFor(ctx.Scope);
        lock (_gate) layer.Suppressors++;
        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate) layer.Suppressors--;
        }));
    }

    /// <summary>
    /// Contribute tool schemas to every assembly.
    /// </summary>
    /// <param name="ctx">The registering context.</param>
    /// <param name="provider">Produces the schemas at assembly time.</param>
    /// <returns>A disposer that withdraws them.</returns>
    public IDisposable ToolProvider(Context ctx, Func<AssembleContext, ToolProviderResult> provider)
    {
        var layer = LayerFor(ctx.Scope);
        lock (_gate) layer.ToolProviders.Add(provider);
        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate) layer.ToolProviders.Remove(provider);
        }));
    }

    /// <summary>
    /// Contribute a value that sections may interpolate as <c>{{name}}</c>.
    /// </summary>
    /// <param name="ctx">The registering context.</param>
    /// <param name="name">The variable name; lowercase, starting with a letter.</param>
    /// <param name="provider">Produces the value at assembly time.</param>
    /// <returns>A disposer that withdraws it.</returns>
    /// <exception cref="InvalidOperationException">The name is not a legal variable name.</exception>
    public IDisposable Variable(Context ctx, string name, Func<AssembleContext, string?> provider)
    {
        if (!VariableName.IsMatch(name))
        {
            throw new InvalidOperationException($"prompt variable \"{name}\" must match [a-z][a-z0-9_]*");
        }

        var layer = LayerFor(ctx.Scope);
        lock (_gate) layer.Variables[name] = provider;
        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate) layer.Variables.Remove(name);
        }));
    }

    /// <summary>
    /// Build the prefix for one request.
    /// </summary>
    /// <param name="context">What the assembly is for.</param>
    /// <returns>The assembled sections, contexts, tools, and variables.</returns>
    public async Task<PromptAssembly> AssembleAsync(AssembleContext context)
    {
        var assembled = Collect(context);

        var final = await Ctx.WaterfallAsync(
            SystemPromptKeys.Assemble,
            context,
            () => Task.FromResult(assembled),
            context.Scope);

        // Re-applied after the chain: a complete section owns the whole prompt, and a
        // suppressed runtime context stays suppressed, whatever a listener returned.
        var completeName = CompleteSectionName(context);
        if (completeName is not null)
        {
            var complete = final.Sections.FirstOrDefault(section =>
                string.Equals(section.Name, completeName, StringComparison.Ordinal));
            if (complete is not null) final = final with { Sections = [complete] };
        }

        if (IsRuntimeContextSuppressed(context.Scope)) final = final with { Contexts = [] };

        return final;
    }

    private string? CompleteSectionName(AssembleContext context)
    {
        foreach (var section in MergedSections(context.Scope))
        {
            if (section.Complete) return section.Name;
        }

        return null;
    }

    private PromptAssembly Collect(AssembleContext context)
    {
        var sections = MergedSections(context.Scope);
        var completeCount = sections.Count(static section => section.Complete);
        if (completeCount > 1)
        {
            throw new InvalidOperationException(
                "more than one prompt section claims to be the complete system prompt");
        }

        var resolvedSections = new List<AssembledSection>();
        if (completeCount == 1)
        {
            var complete = sections.First(static section => section.Complete);
            resolvedSections.Add(new AssembledSection(complete.Name, complete.Text(context)));
        }
        else
        {
            foreach (var section in sections)
            {
                resolvedSections.Add(new AssembledSection(section.Name, section.Text(context)));
            }
        }

        var resolvedContexts = new List<AssembledSection>();
        if (!IsRuntimeContextSuppressed(context.Scope))
        {
            foreach (var section in MergedContexts(context.Scope))
            {
                var text = section.Text(context);
                if (!string.IsNullOrEmpty(text)) resolvedContexts.Add(new AssembledSection(section.Name, text));
            }
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, provider) in MergedVariables(context.Scope))
        {
            var value = provider(context);
            if (value is not null) variables[name] = value;
        }

        var tools = new List<ToolSchema>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in MergedToolProviders(context.Scope))
        {
            foreach (var schema in provider(context).Schemas)
            {
                if (seen.Add(schema.Name)) tools.Add(schema);
            }
        }

        tools.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        return new PromptAssembly(resolvedSections, resolvedContexts, tools, variables);
    }

    private List<PromptSection> MergedSections(ScopeKey? scope)
    {
        lock (_gate)
        {
            var merged = new Dictionary<string, PromptSection>(_global.Sections, StringComparer.Ordinal);
            if (scope is not null && _scoped.TryGetValue(scope, out var layer))
            {
                foreach (var (name, section) in layer.Sections) merged[name] = section;
            }

            return [.. merged.Values.OrderBy(static section => section.Order)
                .ThenBy(static section => section.Name, StringComparer.Ordinal)];
        }
    }

    private List<PromptContextSection> MergedContexts(ScopeKey? scope)
    {
        lock (_gate)
        {
            var merged = new Dictionary<string, PromptContextSection>(_global.Contexts, StringComparer.Ordinal);
            if (scope is not null && _scoped.TryGetValue(scope, out var layer))
            {
                foreach (var (name, section) in layer.Contexts) merged[name] = section;
            }

            return [.. merged.Values.OrderBy(static section => section.Order)
                .ThenBy(static section => section.Name, StringComparer.Ordinal)];
        }
    }

    private Dictionary<string, Func<AssembleContext, string?>> MergedVariables(ScopeKey? scope)
    {
        lock (_gate)
        {
            var merged = new Dictionary<string, Func<AssembleContext, string?>>(_global.Variables, StringComparer.Ordinal);
            if (scope is not null && _scoped.TryGetValue(scope, out var layer))
            {
                foreach (var (name, provider) in layer.Variables) merged[name] = provider;
            }

            return merged;
        }
    }

    private List<Func<AssembleContext, ToolProviderResult>> MergedToolProviders(ScopeKey? scope)
    {
        lock (_gate)
        {
            var providers = new List<Func<AssembleContext, ToolProviderResult>>(_global.ToolProviders);
            if (scope is not null && _scoped.TryGetValue(scope, out var layer)) providers.AddRange(layer.ToolProviders);
            return providers;
        }
    }

    private bool IsRuntimeContextSuppressed(ScopeKey? scope)
    {
        lock (_gate)
        {
            if (_global.Suppressors > 0) return true;
            return scope is not null && _scoped.TryGetValue(scope, out var layer) && layer.Suppressors > 0;
        }
    }

    /// <summary>
    /// Render the assembled sections into the request's system prompt.
    /// </summary>
    /// <param name="assembly">The assembly to render.</param>
    /// <returns>The sections interpolated and joined, empty when they produce nothing.</returns>
    public static string RenderPrompt(PromptAssembly assembly)
    {
        var parts = new List<string>();
        foreach (var section in assembly.Sections)
        {
            var text = Interpolate(section.Text, assembly.Variables, section.Name);
            if (text.Length > 0) parts.Add(text);
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Render the runtime-context sections.
    /// </summary>
    /// <param name="assembly">The assembly to render.</param>
    /// <returns>Each context section, interpolated, with empty ones dropped.</returns>
    public static IReadOnlyList<AssembledSection> RenderContextSections(PromptAssembly assembly)
    {
        var rendered = new List<AssembledSection>();
        foreach (var section in assembly.Contexts)
        {
            var text = Interpolate(section.Text, assembly.Variables, section.Name);
            if (text.Length > 0) rendered.Add(new AssembledSection(section.Name, text));
        }

        return rendered;
    }

    /// <summary>
    /// Join rendered context sections into the snapshot text the model is shown.
    /// </summary>
    /// <param name="sections">The rendered sections.</param>
    /// <returns>The snapshot, or an empty string when there is nothing to say.</returns>
    public static string JoinContextSections(IReadOnlyList<AssembledSection> sections)
    {
        if (sections.Count == 0) return string.Empty;
        var body = string.Join("\n\n", sections.Select(static section => section.Text));
        return body.Length == 0
            ? string.Empty
            : "Current runtime context. This snapshot supersedes earlier runtime-context snapshots.\n\n" + body;
    }

    /// <summary>
    /// Substitute <c>{{name}}</c> placeholders.
    /// </summary>
    /// <param name="text">The text to interpolate.</param>
    /// <param name="variables">The registered values.</param>
    /// <param name="slot">The slot being rendered, named in any failure.</param>
    /// <returns>The interpolated text.</returns>
    /// <exception cref="InvalidOperationException">
    /// A placeholder names something illegal or unregistered. Failing here beats
    /// shipping a prompt with a literal <c>{{name}}</c> in it.
    /// </exception>
    /// <remarks>
    /// Substituted values are not rescanned, so a value that happens to contain
    /// braces cannot inject another placeholder.
    /// </remarks>
    public static string Interpolate(string text, IReadOnlyDictionary<string, string> variables, string slot)
    {
        if (!text.Contains("{{", StringComparison.Ordinal)) return text;

        var result = new StringBuilder();
        var position = 0;
        foreach (Match match in Placeholder.Matches(text))
        {
            result.Append(text, position, match.Index - position);
            var name = match.Groups[1].Value.Trim();
            if (!VariableName.IsMatch(name))
            {
                throw new InvalidOperationException(
                    $"prompt slot \"{slot}\" uses placeholder \"{{{{{name}}}}}\", which is not a legal variable name");
            }

            if (!variables.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException(
                    $"prompt slot \"{slot}\" uses unregistered variable \"{name}\"");
            }

            result.Append(value);
            position = match.Index + match.Length;
        }

        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    /// <summary>Mount the prompt capability.</summary>
    /// <param name="includeHarnessIdentity">Whether to contribute the built-in identity section.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(bool includeHarnessIdentity = true)
        => new FunctionPlugin("system-prompt", ctx =>
        {
            var service = new SystemPromptService(ctx);
            ctx.Provide(SystemPromptKeys.Service, service);
            if (includeHarnessIdentity)
            {
                service.Section(ctx, PromptSection.Fixed(
                    IdentitySection,
                    -100,
                    "You are an AI agent powered by DeepSeek Harness."));
            }

            return Task.CompletedTask;
        });
}
