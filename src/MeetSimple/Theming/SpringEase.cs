using System.Windows;
using System.Windows.Media.Animation;

namespace MeetSimple.Theming;

/// <summary>
/// A damped spring, the motion behind Apple's Dynamic Island: it leaves quickly, overshoots a little and settles.
/// This is the exact solution of an under-damped spring (x'' + 2ζωx' + ω²x = ω²), so it feels physical rather
/// than keyframed. Damping 1 or more gives a smooth landing with no overshoot.
/// Only the activity island uses it; everything else keeps the shared CubicBezierEase.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty = DependencyProperty.Register(
        nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(0.7));

    /// <summary>How fast the spring moves, in radians per animation duration. About 9 settles fully by the end.</summary>
    public static readonly DependencyProperty FrequencyProperty = DependencyProperty.Register(
        nameof(Frequency), typeof(double), typeof(SpringEase), new PropertyMetadata(9.0));

    public SpringEase()
    {
        // EaseIn means "use EaseInCore as written"; the default EaseOut would mirror the curve.
        EasingMode = EasingMode.EaseIn;
    }

    public SpringEase(double damping, double frequency) : this()
    {
        Damping = damping;
        Frequency = frequency;
    }

    public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }
    public double Frequency { get => (double)GetValue(FrequencyProperty); set => SetValue(FrequencyProperty, value); }

    protected override Freezable CreateInstanceCore() => new SpringEase();

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0.0) return 0.0;
        if (normalizedTime >= 1.0) return 1.0;

        double damping = Math.Max(Damping, 0.05);
        double frequency = Math.Max(Frequency, 1.0);
        double position;
        if (damping >= 1.0)
        {
            position = 1.0 - (1.0 + frequency * normalizedTime) * Math.Exp(-frequency * normalizedTime);
        }
        else
        {
            double decay = damping * frequency;
            double ringing = frequency * Math.Sqrt(1.0 - damping * damping);
            position = 1.0 - Math.Exp(-decay * normalizedTime)
                * (Math.Cos(ringing * normalizedTime) + decay / ringing * Math.Sin(ringing * normalizedTime));
        }

        // Blend the last sliver of residual motion into exactly 1.0 so the animation never jumps at its end.
        double landing = Math.Pow(normalizedTime, 10);
        return position + (1.0 - position) * landing;
    }
}
