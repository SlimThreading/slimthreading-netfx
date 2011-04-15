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

namespace SlimThreading {

    internal struct StSpinWait {

        private const int YIELD_FREQUENCY = 4000;
        private static bool isMultiProcessor;
        private int count;

        //
        // Static constructor.
        //

        static StSpinWait() {
            isMultiProcessor = Platform.IsMultiProcessor;
        }

        //
        // Spins once.
        //

        internal void SpinOnce() {
            count = ++count & Int32.MaxValue;
            if (isMultiProcessor) {
                int r = count % YIELD_FREQUENCY;
                if (r > 0) {
                    Platform.SpinWait((int)(1f + (r * 0.032f)));
                } else {
                    Platform.YieldProcessor();
                }
            } else {
                Platform.YieldProcessor();
            }
        }

        //
        // Spins until the specified condition is true.
        //

        internal static void SpinUntil(Func<bool> condition) {
            if (condition == null) {
                throw new ArgumentNullException("condition");
            }
            StSpinWait spinner = new StSpinWait();
            while (!condition()) {
                spinner.SpinOnce();
            }
        }
    }
}
