// Copyright 2011 Carlos Martins
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//  
using System;
using System.Threading;

#pragma warning disable 0420

namespace SlimThreading {


    //
    // Definition of lock recursion policy.
    //

    public enum StLockRecursionPolicy {
        NoRecursion,
        SupportsRecursion
    }

    //
    // Exception thrown when the lock recursive acquisition rules
    // are breaked.
    //

    public class StLockRecursionException : Exception {

        //
        // Constructors.
        //

        public StLockRecursionException() { }
        public StLockRecursionException(string msg) : base(msg) { }
        public StLockRecursionException(string msg, Exception innerEx) : base(msg, innerEx) { }
    }

    //
    // This class holds the owner counter for a reader thread.
    //

    internal class ReaderCounter {

        internal readonly ReaderCounter next;
        internal int tid;
        internal volatile int count;

        //
        // Constructors.
        //

        internal ReaderCounter(int id, ReaderCounter n) {
            next = n;
            tid = id;
            count = 1;
        }

        internal ReaderCounter() {
            tid = 0;
        }
    }

    //
    // This class implements the table of reader recursive acquisition counters.
    //

    internal class ReaderCounterTable {
        private const int HASH_TABLE_SIZE = 32;				// Must be a power of 2
        private const int HASH_TABLE_MASK = HASH_TABLE_SIZE - 1;

        //
        // Value type that holds a volatile reference.
        //

        private struct ReaderCounterVolatile {
            internal volatile ReaderCounter entry;
        }

        //
        // The hash table buckets.
        //

        private ReaderCounterVolatile[] table;

        //
        // Constructor.
        //

        internal ReaderCounterTable() {
            table = new ReaderCounterVolatile[HASH_TABLE_SIZE];
            for (int i = 0; i < HASH_TABLE_SIZE; i++) {
                table[i].entry = new ReaderCounter();
            }
        }

        //
        // Looks up the entry belonging to the specified thread.
        //

        internal ReaderCounter Lookup(int tid) {
            ReaderCounter rc = table[tid & HASH_TABLE_MASK].entry;
            if (rc.tid == tid && rc.count != 0) {
                return rc;
            }
            while ((rc = rc.next) != null) {
                if (rc.tid == tid) {
                    return (rc.count != 0) ? rc : null;
                }
            }
            return null;
        }

        //
        // Adds an entry to the table for the specified thread.
        //

        internal void Add(int tid) {
            int hi = tid & HASH_TABLE_MASK;
            ReaderCounter rc = table[hi].entry;
            do {
                if (rc.count == 0) {
                    rc.tid = tid;
                    rc.count = 1;
                    return;
                }
            } while ((rc = rc.next) != null);
            rc = new ReaderCounter(tid, table[hi].entry);
            table[hi].entry = rc;
        }
    }

    //
    // This class implements a read/upgrade/write lock.
    //
    // NOTE: This class implements the same semantics of the class
    //       System.Threading.ReaderWriterLockSlim of the FCL.
    //

    public sealed class StReadUpgradeWriteLock {

        //
        // This class implements the ait node used with the r/w lock.
        //

        private sealed class WaitNode : StParker {
            internal volatile WaitNode next;
            internal readonly int tid;

            //
            // The constructor.
            //

            internal WaitNode(int id) {
                tid = id;
            }
        }

        //
        // A Fifo queue of wait nodes.
        //

        private struct WaitNodeQueue {

            //
            // The first and last wait nodes.
            //

            internal WaitNode head;
            internal WaitNode tail;

            //
            // Enqueues a wait node at the tail of the queue.
            //

            internal void Enqueue(WaitNode wn) {
                if (head == null) {
                    head = wn;
                } else {
                    tail.next = wn;
                }
                tail = wn;
            }

            //
            // Dequeues a wait node from a non-empty queue.
            //

            internal WaitNode Dequeue() {
                WaitNode wn;
                if ((head = (wn = head).next) == null) {
                    tail = null;
                }
                wn.next = wn;       // mark wait node as unlinked.
                return wn;
            }

            //
            // Returns true if the queue is empty.
            //

            internal bool IsEmpty { get { return head == null; } }

            //
            // Removes the specified wait node from the queue.
            //

            internal void Remove(WaitNode wn) {

                //
                // If the wait node was already removed, return.
                //

                if (wn.next == wn) {
                    return;
                }

                //
                // Compute the previous wait node and perform the remove.
                //

                WaitNode p = head;
                WaitNode pv = null;
                while (p != null) {
                    if (p == wn) {
                        if (pv == null) {
                            if ((head = wn.next) == null) {
                                tail = null;
                            }
                        } else {
                            if ((pv.next = wn.next) == null)
                                tail = pv;
                        }
                        return;
                    }
                    pv = p;
                    p = p.next;
                }
                throw new InvalidOperationException("WaitOne node not found in the queue!");
            }

            /*++
             * 
             * The following methods are used when the queue is used as a
             * wake up list holding locked wait nodes.
             *
             --*/

            //
            // Adds the wait node at the tail of the queue.
            //

            internal void AddToWakeList(WaitNode wn) {
                if (!wn.UnparkInProgress(StParkStatus.Success)) {
                    if (head == null) {
                        head = wn;
                    } else {
                        tail.next = wn;
                    }
                    wn.next = null;
                    tail = wn;
                }
            }

            //
            // Unparks all the wait nodes in the queue.
            //

            internal void UnparkAll() {
                WaitNode p = head;
                while (p != null) {
                    p.Unpark(StParkStatus.Success);
                    p = p.next;
                }
            }
        }

        //
        // Thrad ID used to mark the locks as unowned.
        //

        private const int UNOWNED = 0;

        //
        // The amount of spinning when trying to acquiring the spinlock.
        //

        private const int SPIN_LOCK_SPINS = 100;

        //
        // The recursion policy.
        //

        private readonly bool isReentrant;

        //
        // The amount of spinning done by the first waiter thread
        // before it actually blocks.
        //

        private int spinCount;

        //
        // Queues for readers, upgarders and writers and the reference
        // to the upgrader thread that is waiting to enter write mode.
        //

        private WaitNodeQueue rdQueue;
        private WaitNodeQueue wrQueue;
        private WaitNodeQueue upQueue;
        private WaitNode upgToWrWaiter;

        //
        // The r/w lock state.
        //

        private int readers;			    // Number of active readers
        private volatile int writer;		// Writer thread ID
        private volatile int upgrader;		// Upgrader thread ID

        //
        // The following fields can be accessed without lock, because
        // they aren't actually shared.
        //

        private int wrCount;			    // Write recursion count
        private int upCount;			    // Upgrade recursion count
        private bool upgraderIsReader;		// Upgrader is also a reader

        //
        // The reader owner table can be searched without acquiring
        // the spinlock. However, to add new entries to the table, a
        // thread must hold the spinlock.
        //

        private readonly ReaderCounterTable rdCounts;

        //
        // The spinlock that protects access to the r/w lock shared state.
        //

        private SpinLock slock;

        //
        // Constructors.
        //

        public StReadUpgradeWriteLock(StLockRecursionPolicy policy, int sc) {
            slock = new SpinLock(SPIN_LOCK_SPINS);
            rdQueue = new WaitNodeQueue();
            wrQueue = new WaitNodeQueue();
            upQueue = new WaitNodeQueue();
            rdCounts = new ReaderCounterTable();
            // writer = upgrader = UNOWNED;         
            isReentrant = (policy == StLockRecursionPolicy.SupportsRecursion);
            spinCount = Platform.IsMultiProcessor ? sc : 0;
        }

        public StReadUpgradeWriteLock(StLockRecursionPolicy policy) : this(policy, 0) { }

        public StReadUpgradeWriteLock() : this(StLockRecursionPolicy.NoRecursion, 0) { }

        //
        // Tries to wakeup a waiting writer.
        //
        // NOTE: This method is called with the spinlock held
        //		 and when the r/w lock is in the NonEntered state.
        //       If a waiter is woken, the method releases the spin lock
        //       and returns true; otherwise, the method returns false and
        //       with the spinlock held.
        //

        private bool TryWakeupWriter() {
            while (!wrQueue.IsEmpty) {
                WaitNode w = wrQueue.Dequeue();
                if (w.TryLock()) {

                    //
                    // Become the waiter the next owner of the write lock,
                    // release the spinlock, unpark the waiter and return true.
                    //

                    writer = w.tid;
                    slock.Exit();
                    w.Unpark(StParkStatus.Success);
                    return true;
                }
            }

            //
            // No writer can be released; so, return false with the
            // spinlock held.
            //

            return false;
        }

        //
        // Tries to wakeup a waiting upgrader and/or all the waiting readers.
        //

        private bool TryWakeupUpgraderAndReaders() {

            //
            // Initialize the wakeup list and the released flag.
            //

            WaitNodeQueue wl = new WaitNodeQueue();
            bool released = false;

            //
            // If the upgraders wait queue isn't empty, try to wakeup a
            // waiting upgrader to enter the lock in upgradeable read mode.
            //

            if (!upQueue.IsEmpty) {
                do {
                    WaitNode w = upQueue.Dequeue();
                    if (w.TryLock()) {

                        //
                        // Make the locked waiter as the current upgrader and
                        // add its wait node to the wake up list.
                        //

                        upgrader = w.tid;
                        wl.AddToWakeList(w);
                        released = true;
                        break;
                    }
                } while (!upQueue.IsEmpty);
            }

            //
            // Even when the r/w lock state changes to the upgrade read mode,
            // all the waiting readers can enter the read lock.
            //

            while (!rdQueue.IsEmpty) {
                WaitNode w = rdQueue.Dequeue();
                if (w.TryLock()) {

                    //
                    // Account for one more active reader, add the respective
                    // entry to the reader owners table and add the waiting reader
                    // to the wake up list.
                    //

                    readers++;
                    rdCounts.Add(w.tid);
                    wl.AddToWakeList(w);
                    released = true;
                }
            }

            //
            // If no thread was released, return false holding the
            // spinlock; otherwise, release the spinlock, unpark all
            // threads inserted in the wake up list and return true.
            //

            if (!released) {
                return false;
            }
            slock.Exit();
            wl.UnparkAll();
            return true;
        }

        //
        // Tries to enter the read lock, activating the
        // specified cancellers.
        //

        public bool TryEnterRead(StCancelArgs cargs) {
            int tid = Thread.CurrentThread.ManagedThreadId;
            ReaderCounter rc = rdCounts.Lookup(tid);
            if (!isReentrant) {
                if (tid == writer) {
                    throw new StLockRecursionException("Read after write not allowed");
                }
                if (rc != null) {
                    throw new StLockRecursionException("Recursive read not allowed");
                }
            } else {

                //
                // If this is a recursive enter, increment the recursive
                // acquisition counter and return.
                //

                if (rc != null) {
                    rc.count++;
                    return true;
                }
            }

            //
            // Acquire the spinlock that protected the r/u/w lock shared state.
            //

            slock.Enter();

            //
            // If the current thread is the upgrader, it can also enter
            // the read lock. So, add an entry to the readers table,
            // release the spinlock and return success.
            //

            if (tid == upgrader) {
                rdCounts.Add(tid);
                slock.Exit();
                upgraderIsReader = true;
                return true;
            }

            //
            // The read lock can be entered, if the r/w lock isn't in write
            // mode and no thread is waiting to enter the writer mode.
            // If these conditions are met, increment the number of lock
            // readers, add an entry to the readers table, release the
            // spinlock and return success.
            //

            if (writer == UNOWNED && wrQueue.IsEmpty && upgToWrWaiter == null) {
                readers++;
                rdCounts.Add(tid);
                slock.Exit();
                return true;
            }

            //
            // If the r/w lock is reentrant and the current thread is the
            // current writer, it can also to become a reader. So, increment
            // the number of lock readers, add an entry to the readers table,
            // release the spinlock and return success.
            //

            if (isReentrant && tid == writer) {
                readers++;
                rdCounts.Add(tid);
                slock.Exit();
                return true;
            }

            //
            // The current thread can't enter the read lock immediately.
            // So, if a null timeout was specified, release the spinlock
            // and return failure.
            //

            if (cargs.Timeout == 0) {
                slock.Exit();
                return false;
            }

            //
            // Create a wait node  and insert it in the readers queue.
            // Compute also the amount of spinning.
            //

            int sc = rdQueue.IsEmpty ? spinCount : 0;
            WaitNode wn;
            rdQueue.Enqueue(wn = new WaitNode(tid));

            //
            // Release the spinlock and park the current thread, activating
            // the specified cancellers and spinning if appropriate.
            //

            slock.Exit();
            int ws = wn.Park(sc, cargs);

            //
            // If we entered the read lock, return success.
            //

            if (ws == StParkStatus.Success) {
                return true;
            }

            //
            // The enter attempt was cancelled. So, ensure that the wait node
            // is unlinked from the queue and report the failure appropriately.
            //

            if (wn.next != wn) {
                slock.Enter();
                rdQueue.Remove(wn);
                slock.Exit();
            }
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Enters the read lock unconditionally.
        //

        public void EnterRead() {
            TryEnterRead(StCancelArgs.None);
        }

        //
        // Exits the read lock.
        //

        public void ExitRead() {
            int tid = Thread.CurrentThread.ManagedThreadId;
            ReaderCounter rc = rdCounts.Lookup(tid);
            if (rc == null) {
                throw new StSynchronizationLockException("Mismatched read");
            }

            //
            // If this is a recursive exit of the read lock, decrement
            // the recursive acquisition count and return.
            //

            if (--rc.count > 0) {
                return;
            }

            //
            // The read lock was exited by the current thread. So, if the current
            // thread is also the upgrader clear the *upgraderIsReader* flag
            // and return, because no other waiter thread can be released.
            //

            if (tid == upgrader) {
                upgraderIsReader = false;
                return;
            }

            //
            // Acquire the spinlock that protecteds the r/u/w lock shared state.
            // Then, decrement the number of active readers; if this is not the
            // unique lock reader, release the spin lock and return.
            //

            slock.Enter();
            if (--readers > 0) {
                slock.Exit();
                return;
            }

            //
            // Here, we know that the r/u/w lock doesn't have readers.
            // First, check the current upgarder is waiting to enter the write lock,
            // and, if so, try to release it.
            //

            WaitNode w;
            if ((w = upgToWrWaiter) != null) {
                upgToWrWaiter = null;
                if (w.TryLock()) {
                    writer = w.tid;
                    slock.Exit();
                    w.Unpark(StParkStatus.Success);
                    return;
                }
            }

            //
            // If the r/w lock isn't in the upgrade read mode nor in write
            // mode, try to wake up a waiting writer; failing that, try to
            // wake up a waiting upgrader and/or all waiting readers.
            //

            if (!(upgrader == UNOWNED && writer == UNOWNED &&
                 (TryWakeupWriter() || TryWakeupUpgraderAndReaders()))) {
                slock.Exit();
            }
        }

        //
        // Tries to enter the write lock, activating the specified
        // cancellers.
        //

        public bool TryEnterWrite(StCancelArgs cargs) {
            int tid = Thread.CurrentThread.ManagedThreadId;
            if (!isReentrant) {
                if (tid == writer) {
                    throw new StLockRecursionException("Recursive enter write not allowed");
                }
                if (rdCounts.Lookup(tid) != null) {
                    throw new StLockRecursionException("Write after read not allowed");
                }
            } else {

                //
                // If this is a recursive enter, increment the recursive acquisition
                // counter and return success.
                //

                if (tid == writer) {
                    wrCount++;
                    return true;
                }
            }

            //
            // Acquire the spinlock that protects the r/w lock shared state.
            //

            slock.Enter();

            //
            // If the write lock can be entered - this is, there are no lock
            // readers, no lock writer or upgrader or the current thread is the
            // upgrader -, enter the write lock, release the spinlock and
            // return success.
            //

            if (readers == 0 && writer == UNOWNED && (upgrader == tid || upgrader == UNOWNED)) {
                writer = tid;
                slock.Exit();
                wrCount = 1;
                return true;
            }

            //
            // If the current thread isn't the current upgrader but is reader,
            // release the spinlock and throw the appropriate exception.
            //

            if (tid != upgrader && rdCounts.Lookup(tid) != null) {
                slock.Exit();
                throw new StLockRecursionException("Write after read not allowed");
            }

            //
            // The write lock can't be entered immediately. So, if a null timeout
            // was specified, release the spinlock and return failure.
            //

            if (cargs.Timeout == 0) {
                slock.Exit();
                return false;
            }

            //
            // Create a wait node and insert it in the writers queue.
            // If the current thread isn't the current upgrader, the wait
            // node is inserted in the writer's queue; otherwise, the wait
            // node becomes referenced by the *upgradeToWriteWaiter* field.
            //

            int sc;
            WaitNode wn = new WaitNode(tid);
            if (tid == upgrader) {
                upgToWrWaiter = wn;
                sc = spinCount;
            } else {
                sc = wrQueue.IsEmpty ? spinCount : 0;
                wrQueue.Enqueue(wn);
            }

            //
            // Release spin the lock and park the current thread, activating
            // the specified cancellers and spinning, if appropriate.
            //

            slock.Exit();
            int ws = wn.Park(sc, cargs);

            //
            // If the thread entered the write lock, initialize the recursive
            // acquisition counter and return success.
            //

            if (ws == StParkStatus.Success) {
                wrCount = 1;
                return true;
            }

            //
            // The enter attempted was cancelled. So, if the wait node was
            // already remove from the respective queue, report the failure
            // appropriately. Otherwise, unlink the wait node and, if appropriate,
            // taking into account the other waiters.
            //

            if (wn.next == wn) {
                goto ReportFailure;
            }
            slock.Enter();
            if (wn.next != wn) {
                if (wn == upgToWrWaiter) {
                    upgToWrWaiter = null;
                } else {
                    wrQueue.Remove(wn);

                    //
                    // If the writers queue becomes empty, it is possible that there
                    // is a waiting upgrader or waiting reader threads that can now
                    // proceed.
                    //

                    if (writer == UNOWNED && upgrader == UNOWNED && wrQueue.IsEmpty &&
                        TryWakeupUpgraderAndReaders()) {
                        goto ReportFailure;
                    }
                }
            }
            slock.Exit();
 
         ReportFailure:
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Enters the write lock unconditionally.
        //

        public void EnterWrite() {
            TryEnterWrite(StCancelArgs.None);
        }

        //
        // Exits the write lock.
        //

        public void ExitWrite() {
            int tid = Thread.CurrentThread.ManagedThreadId;
            if (tid != writer) {
                throw new StSynchronizationLockException("Mismatched write");
            }

            //
            // If this is a recursive exit, decrement the recursive acquistion
            // counter and return.
            //

            if (--wrCount > 0) {
                return;
            }

            //
            // Acquire the spinlock that protects the r/w lock shared state.
            //

            slock.Enter();

            //
            // Clear the current write owner and try to wake up an upgrader
            // and all the waiting readers; failing that, try to wake up a
            // waiting writer.
            //

            writer = UNOWNED;
            if (!(upgrader == UNOWNED && (TryWakeupUpgraderAndReaders() || TryWakeupWriter()))) {
                slock.Exit();
            }
        }

        //
        // Tries to enter the lock in upgrade read mode, activating
        // the specified cancellers.
        // 

        public bool TryEnterUpgradeableRead(StCancelArgs cargs) {
            int tid = Thread.CurrentThread.ManagedThreadId;
            ReaderCounter rc = rdCounts.Lookup(tid);
            if (!isReentrant) {
                if (tid == upgrader) {
                    throw new StLockRecursionException("Recursive upgrade not allowed");
                }
                if (tid == writer) {
                    throw new StLockRecursionException("Upgrade after write not allowed");
                }
                if (rc != null) {
                    throw new StLockRecursionException("Upgrade after read not allowed");
                }
            } else {

                //
                // If the current thread is the current upgrader, increment the
                // recursive acquisition counter and return.
                //

                if (tid == upgrader) {
                    upCount++;
                    return true;
                }

                //
                // If the current thread is the current writer, it can also
                // becomes the upgrader. If it is also a reader, it will be
                // accounted as reader on the *upgraderIsReader* flag.
                //

                if (tid == writer) {
                    upgrader = tid;
                    upCount = 1;
                    if (rc != null) {
                        upgraderIsReader = true;
                    }
                    return true;
                }
                if (rc != null) {
                    throw new StLockRecursionException("Upgrade after read not allowed");
                }
            }

            //
            // Acquire the spinlock that protects the r/w lock shared state.
            //

            slock.Enter();

            //
            // If the lock isn't in write or upgrade read mode, the
            // current thread becomes the current upgrader. Then, release
            // the spinlock and return success.
            //

            if (writer == UNOWNED && upgrader == UNOWNED) {
                upgrader = tid;
                slock.Exit();
                upgraderIsReader = false;
                upCount = 1;
                return true;
            }

            //
            // The upgrade read lock can't be acquired immediately.
            // So, if a null timeout was specified, return failure.
            //

            if (cargs.Timeout == 0) {
                slock.Exit();
                return false;
            }

            //
            // Create a wait node and insert it in the upgrader's queue.
            //

            int sc = (upQueue.IsEmpty && wrQueue.IsEmpty) ? spinCount : 0;
            WaitNode wn;
            upQueue.Enqueue(wn = new WaitNode(tid));

            // 
            // Release the spinlock and park the current thread activating
            // the specified cancellers and spinning, if appropriate.
            //

            slock.Exit();
            int ws = wn.Park(sc, cargs);

            //
            // If we acquired the upgrade lock, initialize the recursive
            // acquisition count and the *upgraderIsReader flag and return
            // success.
            //

            if (ws == StParkStatus.Success) {
                upCount = 1;
                upgraderIsReader = false;
                return true;
            }

            //
            // The acquire attemptwas cancelled. So, ensure that the
            // wait node is unlinked from the wait queue and report
            // the failure appropriately.
            //

            if (wn.next != wn) {
                slock.Enter();
                upQueue.Remove(wn);
                slock.Exit();
            }
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Enters the upgradeable read lock unconditionally.
        //

        public void EnterUpgradeableRead() {
            TryEnterUpgradeableRead(StCancelArgs.None);
        }

        //
        // Exits the upgradeable read lock.
        //

        public void ExitUpgradeableRead() {
            int tid = Thread.CurrentThread.ManagedThreadId;
            if (tid != upgrader) {
                throw new StSynchronizationLockException("Mismatched upgrade");
            }

            //
            // If this is a recursive exit, decrement the recursive acquisition
            // counter and return.
            //

            if (--upCount > 0) {
                return;
            }

            //
            // Acquire the spinlock that protects the r/w lock shared state.
            // Then, if the upgrader is also a reader, increment the number
            // of lock readers.
            //

            slock.Enter();
            if (upgraderIsReader) {
                readers++;
            }
            upgrader = UNOWNED;

            //
            // If there are no active readers nor a lock writer, try to wake up
            // a writer; failing that, try to wakeup a waiting upgrader and all
            // the waiting readers. Anyway, release the spinlock.
            //


            if (!(readers == 0 && writer == UNOWNED &&
                 (TryWakeupWriter() || TryWakeupUpgraderAndReaders()))) {
                slock.Exit();
            }
        }
    }
}
