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

	public static class Platform {

		//
		// Yields the processor.
		//

		public static void YieldProcessor() {
            Thread.Yield();
		}

		//
		// Returns true when the current process runs on a single processor machine.
		//

		internal static bool IsSingleProcessor {
			get { return ProcessorCount == 1; }
		}

		//
		// Returns true when the current process runs on a multiprocessor machine.
		//

		internal static bool IsMultiProcessor {
			get { return ProcessorCount > 1; }
		}

		internal static int ProcessorCount {
            get { return Environment.ProcessorCount; }
		}

        //
        // Adjusts the timeout value based on lastTime.
        //

        public static int AdjustTimeout(ref int lastTime, ref int timeout) {
            if (timeout == Timeout.Infinite) {
                return Timeout.Infinite;
            }
            int now = Environment.TickCount;
            int e = (now == lastTime) ? 1 : now - lastTime;
            if (timeout <= e) {
                return (timeout = 0);
            }
            lastTime = now;
            return (timeout -= e);
        }

        //
        // ...
        //

        public static void SpinWait(int count) {
            Thread.SpinWait(count);
        }
	}
}
