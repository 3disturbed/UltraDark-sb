namespace SexyBiscuit.Engine.AI;

/// <summary>Result of ticking a behaviour tree node.</summary>
public enum NodeStatus
{
    /// <summary>The node is still working and wants to be ticked again next frame.</summary>
    Running,

    /// <summary>The node achieved its goal.</summary>
    Success,

    /// <summary>The node could not achieve its goal.</summary>
    Failure,
}

/// <summary>
/// Context handed to every node on each tick — the blackboard the tree reasons over,
/// the controller that owns it, and the frame delta.
/// </summary>
public sealed class BehaviorContext
{
    /// <summary>Shared state for this tree.</summary>
    public Blackboard Blackboard { get; init; } = null!;

    /// <summary>The AI controller running the tree. Null for a tree driven standalone.</summary>
    public AIController? Controller { get; init; }

    /// <summary>Scaled seconds since the previous tick.</summary>
    public float DeltaTime { get; internal set; }
}

/// <summary>
/// Base class for every node in a <see cref="BehaviorTree"/>.
/// </summary>
/// <remarks>
/// A node is ticked repeatedly while it returns <see cref="NodeStatus.Running"/>.
/// <see cref="OnEnter"/> runs on the first tick of a run and <see cref="OnExit"/> when the node
/// stops running, so a node can set up and tear down transient state without tracking its own
/// "have I started" flag.
/// </remarks>
public abstract class BehaviorNode
{
    /// <summary>Name shown in debug output. Defaults to the type name.</summary>
    public string Name { get; set; }

    /// <summary>Status returned by the most recent tick. Useful for tree visualisation.</summary>
    public NodeStatus LastStatus { get; private set; } = NodeStatus.Failure;

    private bool _entered;

    protected BehaviorNode() => Name = GetType().Name;

    /// <summary>Ticks the node, handling enter/exit bookkeeping around <see cref="OnTick"/>.</summary>
    public NodeStatus Tick(BehaviorContext context)
    {
        if (!_entered)
        {
            _entered = true;
            OnEnter(context);
        }

        var status = OnTick(context);
        LastStatus = status;

        if (status != NodeStatus.Running)
        {
            _entered = false;
            OnExit(context, status);
        }

        return status;
    }

    /// <summary>Forces the node and its children back to a not-started state.</summary>
    public virtual void Reset()
    {
        _entered   = false;
        LastStatus = NodeStatus.Failure;
    }

    /// <summary>Called on the first tick of a run.</summary>
    protected virtual void OnEnter(BehaviorContext context) { }

    /// <summary>The node's work. Return Running to be ticked again next frame.</summary>
    protected abstract NodeStatus OnTick(BehaviorContext context);

    /// <summary>Called when the node finishes with Success or Failure.</summary>
    protected virtual void OnExit(BehaviorContext context, NodeStatus status) { }
}

// =============================================================================
// Composites
// =============================================================================

/// <summary>Base class for nodes that own children.</summary>
public abstract class CompositeNode : BehaviorNode
{
    /// <summary>Child nodes, evaluated in order.</summary>
    public List<BehaviorNode> Children { get; } = new();

    protected CompositeNode(params BehaviorNode[] children) => Children.AddRange(children);

    /// <summary>Appends a child and returns this node so calls can be chained.</summary>
    public CompositeNode Add(BehaviorNode child)
    {
        Children.Add(child);
        return this;
    }

    public override void Reset()
    {
        base.Reset();
        foreach (var c in Children) c.Reset();
    }
}

/// <summary>
/// Runs children in order until one fails. Succeeds only when every child succeeds —
/// the behaviour-tree equivalent of a logical AND.
/// </summary>
/// <remarks>
/// The index of the running child is remembered between ticks, so a long-running child does
/// not cause its earlier siblings to re-run every frame.
/// </remarks>
public sealed class Sequence : CompositeNode
{
    private int _current;

    public Sequence(params BehaviorNode[] children) : base(children) { }

    protected override void OnEnter(BehaviorContext context) => _current = 0;

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        while (_current < Children.Count)
        {
            var status = Children[_current].Tick(context);
            if (status == NodeStatus.Running) return NodeStatus.Running;
            if (status == NodeStatus.Failure) return NodeStatus.Failure;
            _current++;
        }

        return NodeStatus.Success;
    }

    public override void Reset()
    {
        base.Reset();
        _current = 0;
    }
}

/// <summary>
/// Runs children in order until one succeeds. Fails only when every child fails —
/// a logical OR, and the usual way to express prioritised fallback behaviour.
/// </summary>
public sealed class Selector : CompositeNode
{
    private int _current;

    public Selector(params BehaviorNode[] children) : base(children) { }

    protected override void OnEnter(BehaviorContext context) => _current = 0;

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        while (_current < Children.Count)
        {
            var status = Children[_current].Tick(context);
            if (status == NodeStatus.Running) return NodeStatus.Running;
            if (status == NodeStatus.Success) return NodeStatus.Success;
            _current++;
        }

        return NodeStatus.Failure;
    }

    public override void Reset()
    {
        base.Reset();
        _current = 0;
    }
}

/// <summary>
/// Ticks every child each frame and combines their results according to
/// <see cref="SuccessThreshold"/> and <see cref="FailureThreshold"/>.
/// </summary>
public sealed class Parallel : CompositeNode
{
    /// <summary>Children that must succeed for the node to succeed. Zero means all of them.</summary>
    public int SuccessThreshold { get; set; }

    /// <summary>Children that must fail for the node to fail. Defaults to one.</summary>
    public int FailureThreshold { get; set; } = 1;

    public Parallel(params BehaviorNode[] children) : base(children) { }

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        int succeeded = 0, failed = 0;

        foreach (var child in Children)
        {
            switch (child.Tick(context))
            {
                case NodeStatus.Success: succeeded++; break;
                case NodeStatus.Failure: failed++;    break;
            }
        }

        int needed = SuccessThreshold > 0 ? SuccessThreshold : Children.Count;
        if (succeeded >= needed) return NodeStatus.Success;
        if (failed    >= Math.Max(1, FailureThreshold)) return NodeStatus.Failure;
        return NodeStatus.Running;
    }
}

// =============================================================================
// Decorators
// =============================================================================

/// <summary>Base class for nodes that wrap exactly one child.</summary>
public abstract class DecoratorNode : BehaviorNode
{
    /// <summary>The wrapped node.</summary>
    public BehaviorNode Child { get; }

    protected DecoratorNode(BehaviorNode child) => Child = child;

    public override void Reset()
    {
        base.Reset();
        Child.Reset();
    }
}

/// <summary>Swaps Success and Failure from the child. Running passes through unchanged.</summary>
public sealed class Inverter : DecoratorNode
{
    public Inverter(BehaviorNode child) : base(child) { }

    protected override NodeStatus OnTick(BehaviorContext context) => Child.Tick(context) switch
    {
        NodeStatus.Success => NodeStatus.Failure,
        NodeStatus.Failure => NodeStatus.Success,
        _                  => NodeStatus.Running,
    };
}

/// <summary>Reports Success whatever the child returns, once the child stops running.</summary>
public sealed class Succeeder : DecoratorNode
{
    public Succeeder(BehaviorNode child) : base(child) { }

    protected override NodeStatus OnTick(BehaviorContext context)
        => Child.Tick(context) == NodeStatus.Running ? NodeStatus.Running : NodeStatus.Success;
}

/// <summary>
/// Re-runs the child a fixed number of times, or forever when <see cref="Count"/> is zero.
/// </summary>
public sealed class Repeater : DecoratorNode
{
    /// <summary>Times to repeat. Zero repeats indefinitely.</summary>
    public int Count { get; set; }

    /// <summary>Stops repeating and reports Failure as soon as the child fails.</summary>
    public bool StopOnFailure { get; set; } = true;

    private int _completed;

    public Repeater(BehaviorNode child, int count = 0) : base(child) => Count = count;

    protected override void OnEnter(BehaviorContext context) => _completed = 0;

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        var status = Child.Tick(context);
        if (status == NodeStatus.Running) return NodeStatus.Running;
        if (status == NodeStatus.Failure && StopOnFailure) return NodeStatus.Failure;

        _completed++;
        if (Count > 0 && _completed >= Count) return NodeStatus.Success;

        Child.Reset();
        return NodeStatus.Running;
    }

    public override void Reset()
    {
        base.Reset();
        _completed = 0;
    }
}

/// <summary>
/// Blocks the child until <see cref="Seconds"/> have passed since it last finished,
/// so an expensive or noisy behaviour cannot retrigger every frame.
/// </summary>
public sealed class Cooldown : DecoratorNode
{
    /// <summary>Cooldown length in scaled seconds.</summary>
    public float Seconds { get; set; }

    private float _remaining;

    public Cooldown(BehaviorNode child, float seconds) : base(child) => Seconds = seconds;

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        if (_remaining > 0f)
        {
            _remaining -= context.DeltaTime;
            return NodeStatus.Failure;
        }

        var status = Child.Tick(context);
        if (status != NodeStatus.Running) _remaining = Seconds;
        return status;
    }

    public override void Reset()
    {
        base.Reset();
        _remaining = 0f;
    }
}

/// <summary>
/// Runs the child only while <see cref="Predicate"/> holds. When the predicate turns false
/// mid-run the child is reset and the node reports Failure.
/// </summary>
public sealed class Condition : DecoratorNode
{
    /// <summary>Guard evaluated before every tick of the child.</summary>
    public Func<BehaviorContext, bool> Predicate { get; }

    public Condition(Func<BehaviorContext, bool> predicate, BehaviorNode child) : base(child)
        => Predicate = predicate;

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        if (!Predicate(context))
        {
            Child.Reset();
            return NodeStatus.Failure;
        }

        return Child.Tick(context);
    }
}

// =============================================================================
// Leaves
// =============================================================================

/// <summary>Runs a one-shot delegate and reports its result.</summary>
public sealed class ActionNode : BehaviorNode
{
    private readonly Func<BehaviorContext, NodeStatus> _action;

    /// <param name="action">Returns the status for this tick. Return Running to be ticked again.</param>
    /// <param name="name">Optional debug name.</param>
    public ActionNode(Func<BehaviorContext, NodeStatus> action, string? name = null)
    {
        _action = action;
        if (name != null) Name = name;
    }

    /// <summary>Wraps a void delegate that always succeeds.</summary>
    public ActionNode(Action<BehaviorContext> action, string? name = null)
        : this(ctx => { action(ctx); return NodeStatus.Success; }, name) { }

    protected override NodeStatus OnTick(BehaviorContext context) => _action(context);
}

/// <summary>Reports Success or Failure from a predicate without doing any work.</summary>
public sealed class ConditionNode : BehaviorNode
{
    private readonly Func<BehaviorContext, bool> _predicate;

    public ConditionNode(Func<BehaviorContext, bool> predicate, string? name = null)
    {
        _predicate = predicate;
        if (name != null) Name = name;
    }

    protected override NodeStatus OnTick(BehaviorContext context)
        => _predicate(context) ? NodeStatus.Success : NodeStatus.Failure;
}

/// <summary>Stays Running for a fixed duration, then succeeds.</summary>
public sealed class WaitNode : BehaviorNode
{
    /// <summary>Wait length in scaled seconds.</summary>
    public float Seconds { get; set; }

    private float _elapsed;

    public WaitNode(float seconds) => Seconds = seconds;

    protected override void OnEnter(BehaviorContext context) => _elapsed = 0f;

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        _elapsed += context.DeltaTime;
        return _elapsed >= Seconds ? NodeStatus.Success : NodeStatus.Running;
    }
}

// =============================================================================
// Tree
// =============================================================================

/// <summary>
/// A behaviour tree: a root node plus the blackboard it reasons over.
/// </summary>
/// <example>
/// <code>
/// var tree = new BehaviorTree(
///     new Selector(
///         new Sequence(
///             new ConditionNode(c =&gt; c.Blackboard.Has("Target"), "SeesTarget"),
///             new MoveToNode("Target", acceptanceRadius: 1.5f),
///             new ActionNode(c =&gt; Attack(), "Attack")),
///         new Sequence(
///             new WaitNode(2f),
///             new ActionNode(c =&gt; PickNewPatrolPoint(c), "Patrol"))));
///
/// tree.Tick(Time.DeltaTime);
/// </code>
/// </example>
public sealed class BehaviorTree
{
    /// <summary>The node ticked each frame.</summary>
    public BehaviorNode Root { get; set; }

    /// <summary>State shared by every node in this tree.</summary>
    public Blackboard Blackboard { get; }

    /// <summary>Context handed to nodes. Its delta is refreshed on every tick.</summary>
    public BehaviorContext Context { get; }

    /// <summary>Status of the most recent tick.</summary>
    public NodeStatus LastStatus { get; private set; } = NodeStatus.Failure;

    public BehaviorTree(BehaviorNode root, Blackboard? blackboard = null, AIController? controller = null)
    {
        Root       = root;
        Blackboard = blackboard ?? new Blackboard();
        Context    = new BehaviorContext { Blackboard = Blackboard, Controller = controller };
    }

    /// <summary>Ticks the root node once.</summary>
    public NodeStatus Tick(float dt)
    {
        Context.DeltaTime = dt;
        LastStatus = Root.Tick(Context);
        return LastStatus;
    }

    /// <summary>Returns the whole tree to a not-started state.</summary>
    public void Reset() => Root.Reset();
}
