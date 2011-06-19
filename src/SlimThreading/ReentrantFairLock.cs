// Copyright 2011 Carlos Martins, Duarte Nunes
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
    // This class implements a reentrant fair lock (same as the .NET Mutex).
    //

    public sealed class StReentrantFairLock : StWaitable, IMonitorLock {
        private readonly StFairLock flock;

        private const int UNOWNED = 0;
        private int owner;
        private int count;

        public StReentrantFairLock(int spinCount) {
            flock = new StFairLock(spinCount);
        }

        public StReentrantFairLock() {
            flock = new StFairLock();
        }

        //
        // Tries to enter the lock immediately.
        //

        public bool TryEnter() {
            return TryEnter(Thread.CurrentThread.ManagedThreadId);
        }

        //
        // Tries to enter the lock, activating the specified cancellers.
        //

        public bool Enter(StCancelArgs cargs) {
            int tid = Thread.CurrentThread.ManagedThreadId;
            
            if (TryEnter(tid)) {
                return true;
            }

            if (flock.Enter(cargs)) {
                owner = tid;
                return true;
            }
            
            return false;
        }

        //
        // Enters the lock unconditionally.
        //

        public void Enter() {
            Enter(StCancelArgs.None);
        }

        //
        // Exits the lock.
        //

        public void Exit() {
            if (Thread.CurrentThread.ManagedThreadId != owner) {
                throw new StSynchronizationLockException();
            }

            if (count != 0) {
                count--;
                return;
            }

            owner = UNOWNED;
            flock.Exit();
        }

        private bool TryEnter(int tid) {
            if (flock.TryEnter()) {
                owner = tid;
                return true;
            }

            if (owner == tid) {
                count++;
                return true;
            }

            return false;
        }

        #region IMonitorLock

        bool IMonitorLock.IsOwned {
            get { return (owner == Thread.CurrentThread.ManagedThreadId); }
        }

        int IMonitorLock.ExitCompletely() {

            //
            // Set the lock's acquisition count to zero (i.e., one pending acquire),
            // release it and return the previous state.
            //

            int pc = count;
            count = 0;
            Exit();
            return pc;
        }

        void IMonitorLock.Reenter(int waitStatus, int pvCount) {

            //
            // If the wait on the condition variable was successful, the lock is
            // owned by the current thread but the "owner" field is not set; 
            // otherwise, we must do a full acquire.
            //

            if (waitStatus == StParkStatus.Success) {
                owner = Thread.CurrentThread.ManagedThreadId;
            } else {
                Enter();
            }

            //
            // Restore the previous state of the lock.
            //

            count = pvCount;
        }

        //
        // Enqueues the specified wait block in the lock's queue as a locked 
        // acquire request. When this method is callead the lock is owned by
        // the current thread.
        //

        void IMonitorLock.EnqueueWaiter(WaitBlock wb) {
            flock.EnqueueLockedWaiter(wb);
        }

        #endregion

        #region Waitable

        internal override bool _AllowsAcquire {
            get { return flock._AllowsAcquire || 
                         owner == Thread.CurrentThread.ManagedThreadId; 
            }
        }

        internal override bool _TryAcquire() {
            return TryEnter();
        }

        internal override bool _Release() {
            if (owner != Thread.CurrentThread.ManagedThreadId) {
                return false;
            }
            Exit();
            return true;
        }

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            return TryEnter() ? null : flock._WaitAnyPrologue(pk, key, ref hint, ref sc);
        }

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint,
                                                     ref int sc) {
            return _AllowsAcquire ? null : flock._WaitAllPrologue(pk, ref hint, ref sc);
        }

        internal override void _WaitEpilogue() {
            owner = Thread.CurrentThread.ManagedThreadId;
        }

        internal override void _UndoAcquire() {
            Exit();
        }

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock hint) {
            flock._CancelAcquire(wb, hint);
        }

        internal override Exception _SignalException {
            get { return new StSynchronizationLockException(); }
        }
        
        #endregion
    }
}
