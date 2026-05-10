import 'package:flutter/material.dart';

import '../design.dart';
import '../theme.dart';

class BrandMark extends StatelessWidget {
  final double size;
  final bool glow;

  const BrandMark({
    super.key,
    required this.size,
    this.glow = true,
  });

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      width: size,
      height: size,
      child: Stack(
        fit: StackFit.expand,
        children: [
          if (glow)
            DecoratedBox(
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                boxShadow: [
                  BoxShadow(
                    color: BonfireColors.accent.withOpacity(0.20),
                    blurRadius: size * 0.45,
                    spreadRadius: size * 0.04,
                  ),
                  BoxShadow(
                    color: Colors.white.withOpacity(0.10),
                    blurRadius: size * 0.22,
                  ),
                ],
              ),
            ),
          Image.asset(
            BrandAssets.logo,
            fit: BoxFit.contain,
            filterQuality: FilterQuality.medium,
          ),
        ],
      ),
    );
  }
}
