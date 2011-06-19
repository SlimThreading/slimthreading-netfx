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
    // This class implements a synchronization event.
    //

    public sealed class StSynchronizationEvent : Mutant {
        
        public StSynchronizationEvent(bool initialState, int spinCount) 
            : base(initialState, spinCount) { }

        public StSynchronizationEvent(bool initialState) : base(initialState, 0) { }

        public StSynchronizationEvent() : base(false, 0) { }

        //
        // Waits until the event is signalled, activating the specified
        // cancellers.
        //
         
        public bool Wait(StCancelArgs cargs) {
            return Acquire(cargs);
        }

        //
        // Waits unconditionaly until the event is signalled.
        //

        public void Wait() {
            Acquire(StCancelArgs.None);
        }

        //
        // Sets the event to the signalled state and returns the
        // previous state.
        //

        public bool Set() {
            return Release();
        }

        //
        // Resets the event to the non-signalled state and returns
        // the previous event state.
        //

        public bool Reset() {
            return _TryAcquire();
        }
    }
}
