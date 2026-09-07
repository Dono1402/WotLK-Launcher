#pragma once

#include <cmath>
#include <stdexcept>

namespace AtlasArmory
{
inline double HastePercent(double timeMultiplier)
{
    if (!std::isfinite(timeMultiplier) || timeMultiplier <= 0.0)
        throw std::runtime_error("Invalid engine time multiplier");
    return (1.0 / timeMultiplier - 1.0) * 100.0;
}
}
