#include "../src/atlas_shop_barrier.h"
#include <atomic>
#include <cassert>
#include <future>
#include <iostream>
#include <thread>

int main()
{
    AtlasShop::WriteBarrier barrier;
    auto first = std::make_shared<int>(1), second = std::make_shared<int>(2);
    barrier.TrackSave(10, first, false);
    barrier.TrackSave(10, second, false);
    assert(!barrier.Acquire(1, 10));
    first.reset();
    assert(!barrier.Acquire(1, 10)); // Every save, not merely the latest/oldest.
    assert(barrier.Acquire(2, 20)); // An unrelated player's saves do not block it.
    assert(barrier.Guarded(2) && !barrier.Guarded(1) && barrier.NamesPaused());
    assert(!barrier.Acquire(3, 30));
    barrier.Release();
    second.reset();
    assert(barrier.Acquire(1, 10));
    barrier.Release();

    auto shared = std::make_shared<int>(3);
    barrier.TrackSave(10, shared, false);
    barrier.TrackSave(20, shared, false);
    auto workerOwner = shared;
    shared.reset();
    assert(!barrier.Acquire(1, 10) && !barrier.Acquire(2, 20));
    workerOwner.reset();
    assert(barrier.Acquire(1, 10));
    barrier.Release();

    auto callback = std::make_shared<int>(4);
    barrier.TrackNameWork(callback);
    assert(!barrier.Acquire(1, 10)); // An earlier asynchronous name check is still pending.
    auto creationTransaction = std::make_shared<int>(5);
    barrier.TrackSave(30, creationTransaction, true);
    callback.reset();
    assert(!barrier.Acquire(1, 10)); // Its later SQL transaction still owns the name write.
    creationTransaction.reset();
    assert(barrier.Acquire(1, 10));
    barrier.Release();

    // Model the DB queue transferring the final owner to a different worker.
    std::promise<void> started, finish;
    auto ready = started.get_future();
    auto release = finish.get_future();
    auto transaction = std::make_shared<int>(6);
    barrier.TrackSave(40, transaction, false);
    std::atomic<bool> committed{false};
    std::thread worker([owned = std::move(transaction), &started, &committed, done = std::move(release)]() mutable {
        started.set_value();
        done.wait();
        committed.store(true);
        owned.reset();
    });
    ready.wait();
    assert(!barrier.Acquire(4, 40));
    finish.set_value();
    worker.join();
    assert(committed.load() && barrier.Acquire(4, 40));
    barrier.Release();
    barrier.PruneExpired();
    assert(!barrier.NamesPaused());
    std::cout << "PASS: multiple saves, unrelated accounts, shared transactions, pending name callbacks, and worker ownership.\n";
}
