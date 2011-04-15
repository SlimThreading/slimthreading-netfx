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
    // This class implements um reentrant fair lock (same as the .NET Mutex).
    //

    public sealed class StReentrantFairLock : StWaitable, IMonitorLock {

        //
        // The associated non-reentrant fair lock.
        //

        private StFairLock flock;

        //
        // The mutex owner and the recursive acquisition count.
        //

        private const int UNOWNED = 0;
        private int owner;
        private int count;


        //
        // Constructors.
        //

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

            //
            // Try to acquire the lock if it is free.
            //

            int tid = Thread.CurrentThread.ManagedThreadId;
            if (flock.head.next == StFairLock.SET &&
                Interlocked.CompareExchange<WaitBlock>(ref flock.head.next, null,
                                        StFairLock.SET) == StFairLock.SET) {
                owner = tid;
                return true;
            }

            //
            // If the current thread is the actual owner, increment
            // the recursive acquisition counter.
            //

            if (owner == tid) {
                count++;
                return true;
            }
            return false;
        }

        //
        // Tries to enter the lock, activating the specified cancellers.
        //

        public bool TryEnter(StCancelArgs cargs) {

            //
            // Try to acquire the lock if it is free.
            //

            int tid = Thread.CurrentThread.ManagedThreadId;
            if (flock.head.next == StFairLock.SET &&
                Interlocked.CompareExchange<WaitBlock>(ref flock.head.next, null,
                                        StFairLock.SET) == StFairLock.SET) {
                owner = tid;
                return true;
            }

            //
            // If the current thread is the actual owner, increment
            // the recursive acquisition counter.
            //

            if (owner == tid) {
                count++;
                return true;
            }

            //
            // The lock is busy; so, return failure if a null
            // timeout was specified.
            //

            if (cargs.Timeout == 0) {
                return false;
            }

            if (flock.TryEnter(cargs)) {
                owner = tid;
                return true;
            }
            return false;
        }

        //
        //
        // Enters the lock unconditionally.
        //

        public void Enter() {
            TryEnter(StCancelArgs.None);
        }

        //
        // Exits the lock.
        //

        public void Exit() {
            if (Thread.CurrentThread.ManagedThreadId != owner) {
                throw new StSynchronizationLockException();
            }

            //
            // If this is a recursive exit, decrement the recursive
            // acquisition counter and return.
            //

            if (count != 0) {
                count--;
                return;
            }

            //
            // The lock becomes free, so, clear the *owner* field
            // and exit the associated non-reentrant lock.
            //

            owner = UNOWNED;
            flock.Exit();
        }

        /*++
         *
         * IMonitorLock interface implementation.
         *
         --*/

        //
        // Returns true if the mutex is owned by the current thread.
        //

        bool IMonitorLock.IsOwned {
            get {
                return (owner == Thread.CurrentThread.ManagedThreadId);
            }
        }

        //
        // Exit completely the lock, returning the lock's state.
        //

        int IMonitorLock.ExitCompletely() {

            //
            // Set the lock's acquisition count to zero (i.e., one pending acquire),
            // release it and return the previous lock's state.
            //

            int pc = count;
            count = 0;
            Exit();
            return pc;
        }

        //
        // Reacquires the lock and restore its previous state.
        //

        void IMonitorLock.Reenter(int waitStatus, int pvCount) {

            //
            // If the wait on condition variable wasn't cancelled,
            // the lock is already owned by the current thread, but
            // the "owner" field is not set; otherwise, we must do
            // a full acquire.
            //

            if (waitStatus == StParkStatus.Success) {
                owner = Thread.CurrentThread.ManagedThreadId;
            } else {
                Enter();
            }

            //
            // Restore the previous state of lock.
            //

            count = pvCount;
        }

        //
        // Enqueues the specified wait block in the lock's queue
        // as a locked acquire request.
        //
        // NOTE: When this method is callead the lock is owned by
        //       the current thread.
        //

        void IMonitorLock.EnqueueWaiter(WaitBlock wb) {
            ((IMonitorLock)flock).EnqueueWaiter(wb);
        }

        /*++
         * 
         * Virtual methods that support the Waitable functionality.
         * 
         --*/

        //
        // Returns true if the lock is free.
        //

        internal override bool _AllowsAcquire {
            get {
                return (flock.head.next == StFairLock.SET ||
                        owner == Thread.CurrentThread.ManagedThreadId);
            }
        }

        //
        // Tries to acquire the lock immediately.
        //

        internal override bool _TryAcquire() {
            return TryEnter();
        }

        //
        // Exits the lock.
        //

        internal override bool _Release() {
            if (owner != Thread.CurrentThread.ManagedThreadId) {
                return false;
            }
            Exit();
            return true;
        }

        //
        // Executes the prologue of the Waitable.WaitAny method.
        //

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc) {
            int tid = Thread.CurrentThread.ManagedThreadId;

            if (TryEnter()) {

                //
                // Try to lock the parker and, if succeed, self unpark the
                // current thread.
                //

                if (pk.TryLock()) {
                    pk.UnparkSelf(key);
                } else {

                    //
                    // The parker was already lock; so, undo release the lock,
                    // undoing the previous acquire.
                    //

                    Exit();
                }
                return null;
            }

            //
            // The lock is busy, so execute the WaitAny prologue on the
            // associated non-reentrant lock.
            //

            return flock._WaitAnyPrologue(pk, key, ref hint, ref sc);
        }

        //
        // Executes the prologue of the Waitable.WaitAll method.
        //

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock hint,
                                                     ref int sc) {
            if (_AllowsAcquire) {

                //
                // The lock can be immediately acquired by the current thread.
                // So, lock the parker and, if this is the last cooperative release,
                // self unpark the current thread.
                //

                if (pk.TryLock()) {
                    pk.UnparkSelf(StParkStatus.StateChange);
                }

                //
                // Return null to signal that no wait block was inserted
                // in the lock's queue.
                //

                return null;
            }

            //
            // The lock is busy; so, execute the WaitAll prologue on the
            // associated non-reentrant lock.
            //

            return flock._WaitAllPrologue(pk, ref hint, ref sc);
        }

        //
        // Executes the epilogue for the Waitable.WaitXxx method, this
        // is, sets the lock's *owner* field with the identifier of the
        // current thread.
        //

        internal override void _WaitEpilogue() {
            owner = Thread.CurrentThread.ManagedThreadId;
        }

        //
        // Exits the lock, undoing a previous enter.
        //

        internal override void _UndoAcquire() {
            Exit();
        }

        //
        // Cancels the specified acquire attempt.
        //

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock hint) {
            flock._CancelAcquire(wb, hint);
        }

        //
        // Return the exception that must be thrown when the release fails.
        //

        internal override Exception _SignalException {
            get { return new StSynchronizationLockException(); }
        }
    }
}
