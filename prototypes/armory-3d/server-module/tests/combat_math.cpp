#include "CombatMath.h"
#include <cassert>
#include <limits>

int main()
{
    assert(std::abs(AtlasArmory::HastePercent(1.0)) < 0.00001);
    assert(std::abs(AtlasArmory::HastePercent(0.8) - 25.0) < 0.00001);
    assert(std::abs(AtlasArmory::HastePercent(1.25) + 20.0) < 0.00001);
    for (double value : {0.0, -1.0, std::numeric_limits<double>::quiet_NaN()})
    {
        bool rejected = false;
        try { AtlasArmory::HastePercent(value); } catch (std::runtime_error const&) { rejected = true; }
        assert(rejected);
    }
}
