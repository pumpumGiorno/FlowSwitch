namespace FlowSwitch.Core.Animation;

/// <summary>
/// A damped spring described the way motion designers think about it:
/// <see cref="Response"/> is the period (seconds) of the undamped oscillation — roughly
/// "how long the motion feels" — and <see cref="DampingRatio"/> is 1 for a critically damped
/// (no overshoot) spring and &lt; 1 for a spring that overshoots slightly.
/// </summary>
public readonly record struct SpringSpec(float Response, float DampingRatio)
{
    /// <summary>Natural angular frequency ω₀ = 2π / response.</summary>
    public float Omega => MathF.Tau / MathF.Max(Response, 1e-4f);

    public SpringSpec Scaled(float responseScale) =>
        new(MathF.Max(0.02f, Response * responseScale), DampingRatio);

    public SpringSpec WithDamping(float dampingRatio) => new(Response, dampingRatio);

    public override string ToString() => $"Spring(response {Response * 1000:0}ms, ζ {DampingRatio:0.00})";
}
