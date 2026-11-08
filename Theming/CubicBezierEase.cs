using System.Windows;
using System.Windows.Media.Animation;

namespace MeetSimple.Theming;

/// <summary>
/// A CSS-style cubic-bezier easing curve. The default (0.65, 0, 0.35, 1) eases in gently, moves quickly
/// through the middle and eases out gently, so nothing ever starts or stops abruptly.
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public static readonly DependencyProperty FirstControlXProperty = DependencyProperty.Register(
        nameof(FirstControlX), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.65));
    public static readonly DependencyProperty FirstControlYProperty = DependencyProperty.Register(
        nameof(FirstControlY), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.0));
    public static readonly DependencyProperty SecondControlXProperty = DependencyProperty.Register(
        nameof(SecondControlX), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.35));
    public static readonly DependencyProperty SecondControlYProperty = DependencyProperty.Register(
        nameof(SecondControlY), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(1.0));

    public double FirstControlX { get => (double)GetValue(FirstControlXProperty); set => SetValue(FirstControlXProperty, value); }
    public double FirstControlY { get => (double)GetValue(FirstControlYProperty); set => SetValue(FirstControlYProperty, value); }
    public double SecondControlX { get => (double)GetValue(SecondControlXProperty); set => SetValue(SecondControlXProperty, value); }
    public double SecondControlY { get => (double)GetValue(SecondControlYProperty); set => SetValue(SecondControlYProperty, value); }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase();

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0.0) return 0.0;
        if (normalizedTime >= 1.0) return 1.0;

        // Find the curve parameter whose x position equals the elapsed time, then read the eased y at that point.
        double lowerBound = 0.0;
        double upperBound = 1.0;
        double curveParameter = normalizedTime;
        for (int iteration = 0; iteration < 28; iteration++)
        {
            curveParameter = (lowerBound + upperBound) / 2.0;
            double horizontalPosition = Bezier(curveParameter, FirstControlX, SecondControlX);
            if (Math.Abs(horizontalPosition - normalizedTime) < 0.00001) break;
            if (horizontalPosition < normalizedTime) lowerBound = curveParameter; else upperBound = curveParameter;
        }
        return Bezier(curveParameter, FirstControlY, SecondControlY);
    }

    private static double Bezier(double curveParameter, double firstControl, double secondControl)
    {
        double inverse = 1.0 - curveParameter;
        return 3.0 * inverse * inverse * curveParameter * firstControl
             + 3.0 * inverse * curveParameter * curveParameter * secondControl
             + curveParameter * curveParameter * curveParameter;
    }
}
