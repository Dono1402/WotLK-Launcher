#include "CaptureSchedule.h"
#include <cassert>
#include <limits>

int main()
{
    using AtlasArmory::CaptureReason;
    using AtlasArmory::CaptureSchedule;
    CaptureSchedule schedule(100);
    assert(schedule.Poll(5099) == CaptureReason::None);
    assert(schedule.Poll(5100) == CaptureReason::Login);
    assert(schedule.Poll(65099) == CaptureReason::None);
    assert(schedule.Poll(65100) == CaptureReason::Periodic);
    assert(schedule.Poll(65100) == CaptureReason::None);
    schedule.EquipmentChanged(66100);
    assert(schedule.Poll(68100) == CaptureReason::None);
    assert(schedule.Poll(70100) == CaptureReason::Equipment);
    schedule.EquipmentChanged(75100);
    schedule.EquipmentChanged(76000);
    assert(schedule.Poll(77999) == CaptureReason::None);
    assert(schedule.Poll(78000) == CaptureReason::Equipment);
    assert(schedule.Poll(83000) == CaptureReason::None);

    CaptureSchedule duringLogin(0);
    duringLogin.EquipmentChanged(4999);
    assert(duringLogin.Poll(5000) == CaptureReason::None);
    assert(duringLogin.Poll(6999) == CaptureReason::Login);

    CaptureSchedule continuousChanges(0);
    assert(continuousChanges.Poll(5000) == CaptureReason::Login);
    for (std::uint64_t now = 6000; now <= 65000; now += 1000)
    {
        continuousChanges.EquipmentChanged(now);
        assert(continuousChanges.Poll(now) == (now == 65000 ? CaptureReason::Equipment : CaptureReason::None));
    }
    assert(continuousChanges.Poll(65000) == CaptureReason::None);

    CaptureSchedule stalled(0);
    assert(stalled.Poll(3600000) == CaptureReason::Login);
    assert(stalled.Poll(3600000) == CaptureReason::None);
    assert(stalled.Poll(3660000) == CaptureReason::Periodic);
    assert(stalled.Poll(1) == CaptureReason::None);

    CaptureSchedule reconnected(3660000);
    assert(reconnected.Poll(3664999) == CaptureReason::None);
    assert(reconnected.Poll(3665000) == CaptureReason::Login);
    auto const end = std::numeric_limits<std::uint64_t>::max();
    CaptureSchedule longUptime(end - 60000);
    assert(longUptime.Poll(end) == CaptureReason::Login);
    assert(longUptime.Poll(end) == CaptureReason::None);
}
