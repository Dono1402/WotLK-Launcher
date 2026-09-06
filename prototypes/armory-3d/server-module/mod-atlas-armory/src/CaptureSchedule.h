#ifndef ATLAS_ARMORY_CAPTURE_SCHEDULE_H
#define ATLAS_ARMORY_CAPTURE_SCHEDULE_H

#include <cstdint>

namespace AtlasArmory
{
enum class CaptureReason { None, Login, Equipment, Periodic };

class CaptureSchedule
{
public:
    explicit CaptureSchedule(std::uint64_t now) : _lastCapture(now), _lastChange(now) { }

    void EquipmentChanged(std::uint64_t now)
    {
        _dirty = true;
        _lastChange = now;
    }

    CaptureReason Poll(std::uint64_t now)
    {
        if (now < _lastCapture || now - _lastCapture < 5000)
            return CaptureReason::None;
        bool const periodic = now - _lastCapture >= 60000;
        if (_dirty && (now < _lastChange || now - _lastChange < 2000) && !periodic)
            return CaptureReason::None;
        CaptureReason const reason = _initial ? CaptureReason::Login :
            _dirty ? CaptureReason::Equipment : periodic ? CaptureReason::Periodic : CaptureReason::None;
        if (reason != CaptureReason::None)
        {
            // Advance even on capture failure: never retry SQL on every game tick.
            _lastCapture = now;
            _initial = false;
            _dirty = false;
        }
        return reason;
    }

private:
    std::uint64_t _lastCapture;
    std::uint64_t _lastChange;
    bool _initial = true;
    bool _dirty = false;
};
}

#endif
