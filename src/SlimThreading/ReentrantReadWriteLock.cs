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
#pragma warning disable 0649

namespace SlimThreading {

    //
    // This class implements a reentrant read/write lock.
    //

    public sealed class StReentrantReadWriteLock : StWaitable, IMonitorLock {

        //
        // This class represents each enrty in the lock owner table.
        //

        private class OwnerEntry {

            //
            // The thread id used when an entry is free.
            //

            internal const int FREE_ENTRY = 0;

            internal volatile OwnerEntry next;
            internal volatile int key;
            internal int count;

            //
            // Constructors.
            //

            internal OwnerEntry(int tid) {
                key = tid;
                count = 1;
            }

            internal OwnerEntry() { }

            //
            // CASes on the *key* field.
            //

            internal bool CasKey(int k, int nk) {
                return (key == k && Interlocked.CompareExchange(ref key, nk, k) == k);
            }

            //
            // Frees the owner entry.
            //

            internal void Free() {
                key = FREE_ENTRY;
            }
        }

        //
        // This class implements the the lock owner table that
        // is a map indexed by the thread id.
        //

        private class LockOwnerTable {

            private const int HASH_TABLE_SIZE = 32;		// must be a power of 2
            private const int HASH_TABLE_MASK = HASH_TABLE_SIZE - 1;

            //
            // The hash table itself.
            //

            private VolatileReference[] table;

            //
            // Constructor.
            //

            internal LockOwnerTable() {
                table = new VolatileReference[HASH_TABLE_SIZE];
            }

            //
            // Looks up for an entry belonging to the specified thread id.
            //

            internal bool LookupEntry(int tid, out OwnerEntry entry) {
                int hi = tid & HASH_TABLE_MASK;
                entry = null;
                OwnerEntry p = table[hi].tail;
                while (p != null) {
                    if (tid == p.key) {
                        entry = p;
                        return true;
                    }
                    if (entry == null && p.key == OwnerEntry.FREE_ENTRY) {
                        entry = p;
                    }
                    p = p.next;
                }
                return false;
            }

            //
            // Allocates an owner entry for the specified thread id.
            //

            internal OwnerEntry AllocateEntry(int tid, OwnerEntry hint) {
                if (hint != null && hint.CasKey(OwnerEntry.FREE_ENTRY, tid)) {
                    hint.count = 1;
                    return hint;
                }
                int hi = tid & HASH_TABLE_MASK;
                OwnerEntry p = table[hi].tail;
                while (p != null) {
                    if (p.CasKey(OwnerEntry.FREE_ENTRY, tid)) {
                        p.count = 1;
                        return p;
                    }
                    p = p.next;
                }
                OwnerEntry ne = new OwnerEntry(tid);
                do {
                    p = table[hi].tail;
                    ne.next = p;
                    if (table[hi].CasTail(p, ne)) {
                        return ne;
                    }
                } while (true);
            }

            //
            // This value type holds a volatile reference.
            //

            private struct VolatileReference {
                internal volatile OwnerEntry tail;

                internal bool CasTail(OwnerEntry t, OwnerEntry nt) {
                    return (tail == t &&
                            Interlocked.CompareExchange<OwnerEntry>(ref tail, nt, t) == t);
                }
            }
        }

        //
        // Constant used for owner thread id when the lock is free.
        //

        private const int UNOWNED = 0;

        //
        // The non-reentrant r/w lock
        //

        private readonly StReadWriteLock rwlock;

        //
        // The current write thread and its recursive acquisition count.
        //

        private int writer;
        private int count;

        //
        // Read owners table.
        //

        private LockOwnerTable readOwner = new LockOwnerTable();

        //
        // First reader thread and its recursive acquisition count.
        //

        private int firstReader;
        private int firstReaderCount;

        //
        // Constructors.
        //

        public StReentrantReadWriteLock(int spinCount) {
            rwlock = new StReadWriteLock(spinCount);
        }

        public StReentrantReadWriteLock() {
            rwlock = new StReadWriteLock(0);
        }

        /*++
         * 
         * Read Lock Section
         * 
         --*/

        //
        // Tries to enter the read lock if this is the first reader
        // or a recursive acquisition.
        //

        private bool FastTryEnterRead(int tid, out OwnerEntry oce) {
            oce = null;

            //
            // Try to enter the read lock as the first reader.
            //

            if (rwlock.state == 0 &&
                Interlocked.CompareExchange(ref rwlock.state, 1, 0) == 0) {
                firstReader = tid;
                firstReaderCount = 1;
                return true;
            }

            //
            // Check if this is a recursive acquisition and the current
            // thread is the first reader.
            //

            if (firstReader == tid) {
                firstReaderCount++;
                return true;
            }

            //
            // Check if the current thread is already a read lock owner.
            //

            if (readOwner.LookupEntry(tid, out oce)) {
                oce.count++;
                return true;
            }
            return false;
        }

        //
        // Tries to enter the read lock, activating the specified
        // cancellers.
        //

        public bool TryEnterRead(StCancelArgs cargs) {
            OwnerEntry hint;
            int tid;

            if (FastTryEnterRead((tid = Thread.CurrentThread.ManagedThreadId), out hint)) {
                return true;
            }

            //
            // The current thread isn't a read lock owner. So, allocate an
            // entry on the lock owner table, before acquire the non-reentrant
            // r/w lock.
            //

            hint = readOwner.AllocateEntry(tid, hint);

            if (rwlock.TryEnterRead(cargs)) {
                return true;
            }

            //
            // The specified timeout expired, so remove our entry
            // from the lock owner table and return failure.
            //

            hint.Free();
            return false;
        }

        //
        // Enters unconditionally the read lock.
        //

        public void EnterRead() {
            TryEnterRead(StCancelArgs.None);
        }

        //
        // Exits the read lock.
        //

        public void ExitRead() {
            int tid;
            if ((tid = Thread.CurrentThread.ManagedThreadId) == firstReader) {
                if (--firstReaderCount == 0) {
                    firstReader = UNOWNED;
                    rwlock.ExitRead();
                }
            } else {
                OwnerEntry oce;
                if (!readOwner.LookupEntry(tid, out oce)) {
                    throw new StSynchronizationLockException();
                }
                if (--oce.count == 0) {
                    rwlock.ExitRead();
                    oce.Free();
                }
            }
        }

        /*++
         * 
         * Write Lock Section
         *
         --*/

        //
        // Tries to enter the write lock, considering only the cases
        // of the free lock or recursive acquisition.
        //

        private bool FastTryEnterWrite(int tid) {

            //
            // Try enter the write lock if it is free.
            //

            if (rwlock.state == 0 &&
                Interlocked.CompareExchange(ref rwlock.state, StReadWriteLock.WRITING, 0) == 0){

                //
                // We entered the write lock, so set the lock owner
                // and initilize the recursive acquisition counter.
                //

                writer = tid;
                count = 1;
                return true;
            }

            //
            // If this is a recursive acquisition, increment the
            // recursive acquisition counter.
            //

            if (writer == tid) {
                count++;
                return true;
            }
            return false;
        }

        //
        // Tries to enter the write lock, activating the specified
        // cancellers.
        //

        public bool TryEnterWrite(StCancelArgs cargs) {
            int tid;
            if (FastTryEnterWrite(tid = Thread.CurrentThread.ManagedThreadId)) {
                return true;
            }

            //
            // We are not the current writer; so, try to enter the write
            // on the associated non-reentrant r/w lock.
            //

            if (rwlock.TryEnterWrite(cargs)) {
                writer = tid;
                count = 1;
                return true;
            }

            //
            // The specified timeout expired, so return failure.
            //

            return false;
        }

        //
        // Enters unconditionally the write lock.
        //

        public void EnterWrite() {
            TryEnterWrite(StCancelArgs.None);
        }

        //
        // Exits the write lock
        //

        public void ExitWrite() {
            if (Thread.CurrentThread.ManagedThreadId != writer) {
                throw new StSynchronizationLockException();
            }
            if (--count == 0) {
                writer = UNOWNED;
                rwlock.ExitWrite();
            }
        }

        /*++
         * 
         * IMonitorLock interface implementation.
         * 
         --*/

        //
        // Returns true if the write lock is owned by the current thread.
        //

        bool IMonitorLock.IsOwned {
            get { return writer == Thread.CurrentThread.ManagedThreadId; }
        }

        //
        // Exits the write lock completely.
        //

        int IMonitorLock.ExitCompletely() {
            int pvCount = count;
            count = 1;
            ExitWrite();
            return pvCount;
        }

        //
        // Reenters the write lock, after the thread is woken in
        // on the condition variable.
        //

        void IMonitorLock.Reenter(int waitStatus, int pvCount) {
            if (waitStatus != StParkStatus.Success) {
                rwlock.EnterWrite();
            }
            writer = Thread.CurrentThread.ManagedThreadId;
            count = pvCount;
        }

        //
        // Adds a writer waiter to the waiting list.
        //
        // NOTE: When this method is called, we know that the write
        //       lock is owned by the current thread.
        //

        void IMonitorLock.EnqueueWaiter(WaitBlock wb) {
            ((IMonitorLock)rwlock).EnqueueWaiter(wb);
        }

        /*++
         * 
         * The virtual methods that support the Waitable functionality.
         * 
         *-*/

        //
        // Returns true if the write lock can be immediately entered.
        //

        internal override bool _AllowsAcquire {
            get {
                return (writer == Thread.CurrentThread.ManagedThreadId || rwlock._AllowsAcquire);
            }
        }

        //
        // Enters the write lock if it is free.
        //

        internal override bool _TryAcquire() {
            return FastTryEnterWrite(Thread.CurrentThread.ManagedThreadId);
        }

        //
        // Releases the write lock.
        //

        internal override bool _Release() {
            if (writer != Thread.CurrentThread.ManagedThreadId) {
                return false;
            }
            ExitWrite();
            return true;
        }

        internal override void _UndoAcquire() {
            ExitWrite();
        }

        //
        // Executes the prologue of the Waitable.WaitAny method.
        //

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            return FastTryEnterWrite(Thread.CurrentThread.ManagedThreadId) 
                 ? null 
                 : rwlock._WaitAnyPrologue(pk, key, ref hint, ref sc);
        }

        //
        // Executes the prologue of the Waitable.WaitAll method.
        //

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint,
                                                     ref int sc) {
            return _AllowsAcquire ? null : rwlock._WaitAllPrologue(pk, ref hint, ref sc);
        }

        //
        // Executes the epilogue of the Waitable.WaitXxx methods.
        //

        internal override void  _WaitEpilogue() {
            writer = Thread.CurrentThread.ManagedThreadId;
            count = 1;
        }

        //
        // Returns the exception that must be thrown when the
        // Waitable.Signal operation fails.
        //

        internal override Exception  _SignalException {
            get { return new StSynchronizationLockException(); }
        }

        //
        // Cancels the specified acquire attempt.
        //

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock hint) {
            rwlock._CancelAcquire(wb, hint);
        }
    }
}
