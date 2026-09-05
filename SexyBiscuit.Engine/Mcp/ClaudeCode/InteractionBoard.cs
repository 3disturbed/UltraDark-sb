using System.Collections.Concurrent;

namespace SexyBiscuit.Engine.Mcp.ClaudeCode;

/// <summary>Who currently owns the Assistant panel's composer.</summary>
public enum InteractionDriver
{
    /// <summary>No session and no external client waiting; typing starts a session.</summary>
    None,

    /// <summary>The editor's own Claude Code process; typing sends it a user message.</summary>
    Embedded,

    /// <summary>A Claude Code in a terminal, waiting in <c>wait_for_user</c>; typing answers it.</summary>
    External,
}

public enum QuestionState
{
    Pending,
    Answered,
    TimedOut,
    Cancelled,
}

/// <summary>The user's reply to <c>ask_user</c>.</summary>
public sealed record QuestionAnswer(string Text, int? ChoiceIndex, bool FreeText);

/// <summary>A question a tool asked and is blocked on.</summary>
public sealed class PendingQuestion
{
    private readonly TaskCompletionSource<QuestionAnswer?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingQuestion(long id, string text, IReadOnlyList<string> choices, bool allowFreeText, TimeSpan timeout, string? client)
    {
        Id            = id;
        Text          = text;
        Choices       = choices;
        AllowFreeText = allowFreeText;
        Timeout       = timeout;
        Client        = client;
        AskedUtc      = DateTime.UtcNow;
    }

    public long                  Id            { get; }
    public string                Text          { get; }
    public IReadOnlyList<string> Choices       { get; }
    public bool                  AllowFreeText { get; }
    public TimeSpan              Timeout       { get; }
    public DateTime              AskedUtc      { get; }
    public string?               Client        { get; }

    public QuestionState   State  { get; private set; } = QuestionState.Pending;
    public QuestionAnswer? Answer { get; private set; }

    internal Task<QuestionAnswer?> Completion => _completion.Task;

    /// <summary>Answers with a choice index, free text, or both. False when already resolved.</summary>
    public bool TryAnswer(string text, int? choiceIndex)
    {
        if (choiceIndex is { } index && (index < 0 || index >= Choices.Count)) return false;

        string finalText = choiceIndex is { } i ? Choices[i] : text;
        var answer = new QuestionAnswer(finalText, choiceIndex, choiceIndex == null);
        if (!_completion.TrySetResult(answer)) return false;

        Answer = answer;
        State  = QuestionState.Answered;
        return true;
    }

    internal bool TryResolve(QuestionState state)
    {
        if (!_completion.TrySetResult(null)) return false;
        State = state;
        return true;
    }
}

public enum WaitStatus
{
    Prompt,
    Timeout,
    Cancelled,
    EditorClosing,
}

public enum BoardEventKind
{
    Said,
    QuestionAsked,
    QuestionResolved,
    PromptQueued,
    PromptConsumed,
    WaitStarted,
    WaitEnded,
}

/// <summary>Something the panel should show, raised from a tool thread and drained on the main thread.</summary>
public sealed record BoardEvent(BoardEventKind Kind, string? Text, string? Level, PendingQuestion? Question, string? Client);

/// <summary>
/// The state behind <c>say</c>, <c>ask_user</c> and <c>wait_for_user</c>: a queue of prompts the
/// user typed, questions tools are blocked on, and who is driving. Thread-safe; tools call it
/// from request threads and the panel drains <see cref="TryDequeueEvent"/> every frame.
/// </summary>
public sealed class InteractionBoard
{
    private readonly object _lock = new();
    private readonly LinkedList<string> _prompts = new();
    private readonly Queue<TaskCompletionSource<string?>> _waiters = new();
    private readonly List<PendingQuestion> _questions = new();
    private readonly ConcurrentQueue<BoardEvent> _events = new();
    private long _nextQuestionId;
    private WaitStatus _cancelStatus = WaitStatus.Cancelled;

    /// <summary>How long an unseen external client keeps counting as the driver.</summary>
    public static readonly TimeSpan ExternalGrace = TimeSpan.FromSeconds(120);

    /// <summary>Set by the host while its own Claude Code process is alive.</summary>
    public bool EmbeddedSessionActive { get; set; }

    /// <summary>When an external (non-embedded) MCP client was last heard from.</summary>
    public DateTime LastExternalSeenUtc { get; set; } = DateTime.MinValue;

    public int PendingPromptCount
    {
        get { lock (_lock) return _prompts.Count; }
    }

    /// <summary>True while at least one <c>wait_for_user</c> is blocked.</summary>
    public bool IsWaitingForPrompt
    {
        get { lock (_lock) return _waiters.Count > 0; }
    }

    /// <summary>The oldest unanswered question, if any.</summary>
    public PendingQuestion? PendingQuestion
    {
        get { lock (_lock) return _questions.FirstOrDefault(q => q.State == QuestionState.Pending); }
    }

    public IReadOnlyList<PendingQuestion> PendingQuestions
    {
        get { lock (_lock) return _questions.Where(q => q.State == QuestionState.Pending).ToArray(); }
    }

    public InteractionDriver Driver
    {
        get
        {
            if (EmbeddedSessionActive) return InteractionDriver.Embedded;
            if (IsWaitingForPrompt || DateTime.UtcNow - LastExternalSeenUtc < ExternalGrace) return InteractionDriver.External;
            return InteractionDriver.None;
        }
    }

    // -------------------------------------------------------------------------
    // say
    // -------------------------------------------------------------------------

    public void Say(string message, string? level, string? client)
        => _events.Enqueue(new BoardEvent(BoardEventKind.Said, message, level, null, client));

    // -------------------------------------------------------------------------
    // Prompts (wait_for_user)
    // -------------------------------------------------------------------------

    /// <summary>The user typed something for an external session. Handed straight to a waiter when one is blocked.</summary>
    public void QueuePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        TaskCompletionSource<string?>? waiter = null;
        lock (_lock)
        {
            while (_waiters.Count > 0 && waiter == null)
            {
                var candidate = _waiters.Dequeue();
                if (!candidate.Task.IsCompleted) waiter = candidate;
            }

            if (waiter == null) _prompts.AddLast(text);
        }

        if (waiter != null)
        {
            _events.Enqueue(new BoardEvent(BoardEventKind.PromptConsumed, text, null, null, null));
            waiter.TrySetResult(text);
        }
        else
        {
            _events.Enqueue(new BoardEvent(BoardEventKind.PromptQueued, text, null, null, null));
        }
    }

    /// <summary>Removes a queued prompt the user withdrew. False when it was already consumed.</summary>
    public bool WithdrawPrompt(string text)
    {
        lock (_lock) return _prompts.Remove(text);
    }

    /// <summary>
    /// Waits for the next prompt. A queued prompt returns immediately; otherwise blocks until
    /// one arrives, the timeout passes, the wait is cancelled, or the editor closes. A prompt
    /// handed over just as the wait was cancelled is put back at the front so it is not lost.
    /// </summary>
    public async Task<(WaitStatus Status, string? Text)> WaitForPromptAsync(TimeSpan timeout, CancellationToken cancellation, string? client = null)
    {
        TaskCompletionSource<string?> waiter;
        lock (_lock)
        {
            if (_prompts.Count > 0)
            {
                string text = _prompts.First!.Value;
                _prompts.RemoveFirst();
                _events.Enqueue(new BoardEvent(BoardEventKind.PromptConsumed, text, null, null, client));
                return (WaitStatus.Prompt, text);
            }

            waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        _events.Enqueue(new BoardEvent(BoardEventKind.WaitStarted, null, null, null, client));

        try
        {
            using var timer = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellation);
            using var registration = linked.Token.Register(() => waiter.TrySetCanceled());

            string? prompt;
            try
            {
                prompt = await waiter.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The prompt may have landed between the cancellation and this line.
                if (waiter.Task.IsCompletedSuccessfully && waiter.Task.Result is { } late)
                {
                    lock (_lock) _prompts.AddFirst(late);
                }

                if (cancellation.IsCancellationRequested) return (_cancelStatus, null);
                if (timer.IsCancellationRequested)        return (WaitStatus.Timeout, null);
                return (_cancelStatus, null);
            }

            if (prompt == null) return (_cancelStatus, null);
            return (WaitStatus.Prompt, prompt);
        }
        finally
        {
            lock (_lock) RemoveWaiter(waiter);
            _events.Enqueue(new BoardEvent(BoardEventKind.WaitEnded, null, null, null, client));
        }
    }

    private void RemoveWaiter(TaskCompletionSource<string?> waiter)
    {
        if (_waiters.Count == 0) return;
        var remaining = _waiters.Where(w => !ReferenceEquals(w, waiter)).ToArray();
        _waiters.Clear();
        foreach (var w in remaining) _waiters.Enqueue(w);
    }

    // -------------------------------------------------------------------------
    // Questions (ask_user)
    // -------------------------------------------------------------------------

    /// <summary>Posts a question. The panel shows it; <see cref="Answer"/> or the entry itself resolves it.</summary>
    public PendingQuestion Ask(string text, IReadOnlyList<string>? choices, bool allowFreeText, TimeSpan timeout, string? client = null)
    {
        PendingQuestion question;
        lock (_lock)
        {
            question = new PendingQuestion(++_nextQuestionId, text, choices ?? Array.Empty<string>(), allowFreeText, timeout, client);
            _questions.Add(question);
            if (_questions.Count > 50) _questions.RemoveAll(q => q.State != QuestionState.Pending && _questions.IndexOf(q) < _questions.Count - 50);
        }

        _events.Enqueue(new BoardEvent(BoardEventKind.QuestionAsked, text, null, question, client));
        return question;
    }

    /// <summary>Asks and waits. Null means the question timed out; cancellation throws after marking it cancelled.</summary>
    public async Task<QuestionAnswer?> AskAsync(string text, IReadOnlyList<string>? choices, bool allowFreeText, TimeSpan timeout, CancellationToken cancellation, string? client = null)
    {
        var question = Ask(text, choices, allowFreeText, timeout, client);

        using var timer  = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellation);

        var finished = await Task.WhenAny(question.Completion, Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)).ConfigureAwait(false);

        if (finished != question.Completion)
        {
            if (cancellation.IsCancellationRequested)
            {
                question.TryResolve(QuestionState.Cancelled);
                _events.Enqueue(new BoardEvent(BoardEventKind.QuestionResolved, null, null, question, client));
                cancellation.ThrowIfCancellationRequested();
            }

            question.TryResolve(QuestionState.TimedOut);
            _events.Enqueue(new BoardEvent(BoardEventKind.QuestionResolved, null, null, question, client));
            return null;
        }

        var answer = await question.Completion.ConfigureAwait(false);
        _events.Enqueue(new BoardEvent(BoardEventKind.QuestionResolved, answer?.Text, null, question, client));
        return answer;
    }

    /// <summary>Answers a question by id. False when unknown or already resolved.</summary>
    public bool Answer(long questionId, string text, int? choiceIndex)
    {
        PendingQuestion? question;
        lock (_lock) question = _questions.FirstOrDefault(q => q.Id == questionId);
        return question != null && question.TryAnswer(text, choiceIndex);
    }

    // -------------------------------------------------------------------------
    // Shutdown
    // -------------------------------------------------------------------------

    /// <summary>Releases every waiter and question; <paramref name="editorClosing"/> tells waiters why.</summary>
    public void CancelAll(bool editorClosing = false)
    {
        List<TaskCompletionSource<string?>> waiters;
        List<PendingQuestion> questions;
        lock (_lock)
        {
            _cancelStatus = editorClosing ? WaitStatus.EditorClosing : WaitStatus.Cancelled;
            waiters   = _waiters.ToList();
            _waiters.Clear();
            questions = _questions.Where(q => q.State == QuestionState.Pending).ToList();
        }

        foreach (var w in waiters) w.TrySetResult(null);
        foreach (var q in questions)
        {
            if (q.TryResolve(QuestionState.Cancelled))
                _events.Enqueue(new BoardEvent(BoardEventKind.QuestionResolved, null, null, q, q.Client));
        }
    }

    // -------------------------------------------------------------------------
    // Events for the panel
    // -------------------------------------------------------------------------

    public bool TryDequeueEvent(out BoardEvent? boardEvent) => _events.TryDequeue(out boardEvent);
}
