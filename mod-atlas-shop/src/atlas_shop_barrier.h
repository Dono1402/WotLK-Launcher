#pragma once
#include <algorithm>
#include <cstdint>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace AtlasShop
{
// A SQL operation owns its transaction until after COMMIT/ROLLBACK. Tracking
// weak references also covers shared transactions and several outstanding saves.
class WriteBarrier
{
public:
    void TrackSave(std::uint32_t guid, std::shared_ptr<void> const& transaction, bool nameWrite)
    {
        std::lock_guard<std::mutex> lock(_mutex);
        auto& saves = _saves[guid];
        Prune(saves);
        saves.emplace_back(transaction);
        if (nameWrite) { Prune(_names); _names.emplace_back(transaction); }
    }
    void TrackNameWork(std::shared_ptr<void> const& work)
    {
        std::lock_guard<std::mutex> lock(_mutex);
        Prune(_names); _names.emplace_back(work);
    }
    bool Acquire(std::uint32_t account, std::uint32_t guid)
    {
        std::lock_guard<std::mutex> lock(_mutex);
        Prune(_names);
        auto found = _saves.find(guid);
        if (found != _saves.end())
        {
            Prune(found->second);
            if (!found->second.empty()) return false;
            _saves.erase(found);
        }
        if (!account || _account || !_names.empty()) return false;
        _account = account;
        return true;
    }
    void Release() { std::lock_guard<std::mutex> lock(_mutex); _account = 0; }
    bool Guarded(std::uint32_t account) const
    {
        std::lock_guard<std::mutex> lock(_mutex);
        return _account && _account == account;
    }
    bool NamesPaused() const { std::lock_guard<std::mutex> lock(_mutex); return _account != 0; }
    void PruneExpired()
    {
        std::lock_guard<std::mutex> lock(_mutex);
        Prune(_names);
        for (auto it = _saves.begin(); it != _saves.end();)
        {
            Prune(it->second);
            if (it->second.empty()) it = _saves.erase(it); else ++it;
        }
    }
private:
    static void Prune(std::vector<std::weak_ptr<void>>& references)
    {
        references.erase(std::remove_if(references.begin(), references.end(),
            [](auto const& reference) { return reference.expired(); }), references.end());
    }
    mutable std::mutex _mutex;
    std::uint32_t _account = 0;
    std::unordered_map<std::uint32_t, std::vector<std::weak_ptr<void>>> _saves;
    std::vector<std::weak_ptr<void>> _names;
};
}
