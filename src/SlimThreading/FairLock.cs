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
    // This class implements a fair lock.
    //

    public sealed class StFairLock : Mutant, IMonitorLock {

        //
        // Constructors.
        //

        public StFairLock(int spinCount) : base(true, spinCount) {}

        public StFairLock() : base(true, 0) { }

        //
        // Tries to enter the lock immediately.
        //

        public bool TryEnter() {

            return (head.next == SET &&
                    Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET);
        }

        //
        // Tries to enter the lock, activating the specified cancellers.
        //

        public bool TryEnter(StCancelArgs cargs) {
            if (head.next == SET &&
                Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET) {
                return true;
            }
            return (cargs.Timeout != 0) ? SlowTryAcquire(cargs) : false;
        }

        //
        //
        // Enters the lock unconditionally.
        //

        public void Enter() {
            if (head.next == SET &&
                Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET) {
                return;
            }
            SlowTryAcquire(StCancelArgs.None);
        }

        //
        // Exits the lock.
        //

        public void Exit() {

            //
            // If the queue is empty, release the lock immediately.
            //

            if (head.next == null &&
                Interlocked.CompareExchange<WaitBlock>(ref head.next, SET, null) == null) {
                return;
            }

            SlowRelease();
        }

        /*++
         *
         * IMonitorLock interface implementation.
         *
         --*/

        //
        // Returns true if the lock is busy.
        //

        bool IMonitorLock.IsOwned {
            get { return (head.next != SET); }
        }

        //
        // Exits the lock.
        //

        int IMonitorLock.ExitCompletely() {
            Exit();
            return 0;
        }

        //
        // Reacquires the lock.
        //

        void IMonitorLock.Reenter(int waitStatus, int ignored) {

            //
            // If the wait on condition variable wasn't cancelled,
            // the lock is already owned by the current thread; otherwise,
            // we must do an acquire.
            //

            if (waitStatus != StParkStatus.Success) {
                Enter();
            }
        }

        //
        // Enqueues the specified wait block in the lock's queue
        // as a locked acquire request.
        //

        void IMonitorLock.EnqueueWaiter(WaitBlock wb) {
            EnqueueLockedWaiter(wb);
        }
        //
        // Return the exception that must be thrown when the release fails.
        //

        internal override Exception _SignalException {
            get { return new StSynchronizationLockException(); }
        }
    }
}
