/// A subtly flickering flame icon — used wherever the brand wants to feel
/// "alive" rather than a static Material glyph. Two layered effects:
///   • micro-pulse on opacity (1.0 ↔ 0.78) on a 1.4s loop
///   • slow color drift between accent and accent-hover (warmer tip)
/// Both run cheaply on the same AnimationController.

import 'dart:math';
import 'package:flutter/material.dart';
import '../theme.dart';

class AnimatedFlame extends StatefulWidget {
  final double size;
  final Color baseColor;
  final Color hotColor;
  const AnimatedFlame({
    super.key,
    this.size = 24,
    this.baseColor = BonfireColors.accent,
    this.hotColor = BonfireColors.accentHover,
  });

  @override
  State<AnimatedFlame> createState() => _AnimatedFlameState();
}

class _AnimatedFlameState extends State<AnimatedFlame>
    with SingleTickerProviderStateMixin {
  late final AnimationController _c;
  // A small handful of seeded offsets keeps the flicker irregular without
  // needing per-frame randomness.
  late final List<double> _wobbles;

  @override
  void initState() {
    super.initState();
    _c = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 1400),
    )..repeat();
    final rng = Random(0xb04f12e);
    _wobbles = List.generate(8, (_) => rng.nextDouble());
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _c,
      builder: (_, __) {
        // Combine two sines of slightly different frequency for organic motion.
        final t = _c.value * 2 * pi;
        final pulse = 0.78 + 0.22 *
            ((sin(t) + sin(t * 1.7 + _wobbles[0]) * 0.6 + 1.6) / 3.2);
        final colorMix = 0.5 + 0.5 *
            sin(t * 1.3 + _wobbles[1]);
        final color = Color.lerp(widget.baseColor, widget.hotColor, colorMix)!;
        return Opacity(
          opacity: pulse.clamp(0.5, 1.0),
          child: Stack(
            alignment: Alignment.center,
            children: [
              // Soft amber halo — gives the flame a sense of giving off heat.
              Container(
                width: widget.size * 1.7,
                height: widget.size * 1.7,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  gradient: RadialGradient(
                    colors: [
                      color.withOpacity(0.18 * pulse),
                      Colors.transparent,
                    ],
                  ),
                ),
              ),
              Icon(Icons.local_fire_department,
                  color: color, size: widget.size),
            ],
          ),
        );
      },
    );
  }
}
