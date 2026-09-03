using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Cordis;
using Dsh.Interaction;
using Dsh.Tools;

namespace Dsh.App.Core;

/// <summary>One question waiting for a person.</summary>
/// <param name="ToolName">The tool that asked.</param>
/// <param name="Reason">Why it is asking, in the model's own words.</param>
/// <param name="Detail">The command or path the question is about, when there is one.</param>
public sealed record PendingApproval(string ToolName, string? Reason, string? Detail);

/// <summary>
/// The approval channel, as the composer takeover a person answers through.
/// </summary>
/// <remarks>
/// Deliberately a takeover rather than a dialog: it appears where the person is
/// already looking and where they would otherwise be typing, so it cannot be missed
/// or dismissed by reflex, and the conversation behind it stays readable while they
/// decide.
///
/// Registering this is what gives the app the ability to grant anything at all. A
/// front-end with no answerer leaves every privileged action refused, which is the
/// safe default.
/// </remarks>
public sealed partial class ApprovalViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _registration;
    private readonly Func<Action, Task> _toUiThread;
    private TaskCompletionSource<ApprovalOutcome>? _pending;

    [ObservableProperty]
    private PendingApproval? _question;

    /// <param name="ctx">The context to register the answerer on.</param>
    /// <param name="toUiThread">
    /// Marshals work onto the thread that owns the view, since the question arrives on
    /// whichever thread the turn is running on.
    /// </param>
    public ApprovalViewModel(Context ctx, Func<Action, Task>? toUiThread = null)
    {
        _toUiThread = toUiThread ?? (action =>
        {
            action();
            return Task.CompletedTask;
        });

        _registration = ctx.OnWaterfall(ApprovalEvents.Request, AskAsync);
    }

    /// <summary>Whether a question is waiting, which is what hides the composer.</summary>
    public bool IsWaiting => Question is not null;

    /// <inheritdoc />
    partial void OnQuestionChanged(PendingApproval? value) => OnPropertyChanged(nameof(IsWaiting));

    private async Task<ApprovalOutcome> AskAsync(ApprovalQuestion question, Func<Task<ApprovalOutcome>> next)
    {
        var completion = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;

        await _toUiThread(() => Question = new PendingApproval(
            question.Request.ToolName,
            question.Request.Reason,
            question.Request.CallId));

        // A withdrawn question must stop waiting: the turn it belonged to is gone, and
        // leaving the panel up would ask a person about work that is no longer running.
        await using var cancellation = question.CancellationToken.Register(
            () => completion.TrySetResult(ApprovalOutcome.Cancelled));

        try
        {
            return await completion.Task;
        }
        finally
        {
            _pending = null;
            await _toUiThread(() => Question = null);
        }
    }

    /// <summary>Grant the pending action, this once.</summary>
    public void AllowOnce() => Answer(ApprovalOutcome.AllowedOnce);

    /// <summary>Refuse the pending action.</summary>
    public void Reject() => Answer(ApprovalOutcome.Rejected);

    private void Answer(ApprovalOutcome outcome)
    {
        // An answer that does not land leaves the panel up rather than silently
        // dropping the question, so the person can try again.
        _pending?.TrySetResult(outcome);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _registration.Dispose();
        _pending?.TrySetResult(ApprovalOutcome.Cancelled);
    }
}
