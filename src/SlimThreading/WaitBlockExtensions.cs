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

namespace SlimThreading {

    //
    // Miscellaneous extension methods dealing with wait blocks.
    //

    internal static class WaitBlockExtensions
    {
        //
        // Removes and returns the last wait block on the list.
        // We assume there's at least two wait blocks on the list.
        //

        internal static WaitBlock RemoveLast(this WaitBlock wb)
        {
            WaitBlock pv = wb;
            WaitBlock n;

            while ((n = pv.next) != null && n.next != null) {
                pv = n;
            }

            pv.next = null;
            return n;
        }

        //
        // Tries to lock the parker object associated with the specified
        // wait block, unparking the thread if successful.
        //

        internal static bool TryLockAndUnpark(this WaitBlock wb)
        {
            StParker pk;
            if ((pk = wb.parker).TryLock()) {
                pk.Unpark(wb.waitKey);
                return true;
            }
            return false;
        }
    }
}
