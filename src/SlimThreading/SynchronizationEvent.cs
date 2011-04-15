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
    // This class implements a synchronization event.
    //

    public sealed class StSynchronizationEvent : Mutant {

        //
        // Constructors.
        //

        public StSynchronizationEvent(bool initialState, int spinCount) :
                        base(initialState, spinCount) {}

        public StSynchronizationEvent(bool initialState) : base(initialState, 0) { }

        public StSynchronizationEvent() : base(false, 0) { }

        //
        // Waits until the event is signalled, activating the specified
        // cancellers.
        //

        public bool Wait(StCancelArgs cargs) {

            //
            // If the event is signalled, try to reset it and, if succed,
            // return success. Otherwise, wait on the mutant.
            //

            if (head.next == SET &&
                Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET) {
                return true;
            }
            return (cargs.Timeout != 0) ? SlowTryAcquire(cargs) : false;
        }

        //
        // Waits unconditionaly until the event is signalled.
        //

        public void Wait() {

            //
            // If the event is signalled, try to reset it and, if succed,
            // return success. Otherwise, wait on the mutant.
            //

            if (head.next == SET &&
                Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET) {
                return;
            }
            SlowTryAcquire(StCancelArgs.None);
        }

        //
        // Sets the event to the signalled state and returns the
        // previous event's state.
        //

        public bool Set() {
            WaitBlock n;
            if ((n = head.next) == SET) {
                return true;
            }
            if (n == null &&
                Interlocked.CompareExchange<WaitBlock>(ref head.next, SET, null) == null) {
                return false;
            }
            return SlowRelease();
        }

        //
        // Resets the event to the non-signalled state and returns
        // the previous event state.
        //

        public bool Reset() {
            return (head.next == SET &&
                    Interlocked.CompareExchange<WaitBlock>(ref head.next, null, SET) == SET);
        }
    }
}
