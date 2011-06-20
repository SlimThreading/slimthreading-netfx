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

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This class implements a fair lock.
    //

    public sealed class StFairLock : Mutant, IMonitorLock {

        public StFairLock(int spinCount) : base(true, spinCount) { }

        public StFairLock() : base(true, 0) { }

        //
        // Exits the lock.
        //

        public void Exit() {
            Release();
        }

        #region IMonitorLock

        bool IMonitorLock.IsOwned {
            get { return !_AllowsAcquire; }
        }

        int IMonitorLock.ExitCompletely() {
            Exit();
            return 0;
        }

        void IMonitorLock.Reenter(int waitStatus, int ignored) {
            
            //
            // If the wait on the condition variable was successful, the lock is
            // owned by the current thread; otherwise, we must do a full acquire.
            //

            if (waitStatus != StParkStatus.Success) {
                WaitOne();
            }
        }

        void IMonitorLock.EnqueueWaiter(WaitBlock wb) {
            EnqueueLockedWaiter(wb);
        }

        #endregion
    }
}
