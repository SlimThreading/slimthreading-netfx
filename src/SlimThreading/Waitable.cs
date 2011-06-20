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
    // This is the abstract base class for all waitable synchronizers.
    //

    public abstract class StWaitable {

        //
        // Seed used to generate identifiers that don't need to be strictly
        // unique, and the identifier shared by all notification events.
        //

        static private int idSeed = Int32.MinValue;
        internal const int NOTIFICATION_EVENT_ID = Int32.MaxValue;

        //
        // The synchronizer ID is used to sort the synchronizers
        // in the WaitAll method in order to prevent livelock.
        //

        internal volatile int id;
          
        //
        // Returns the "unique" waitable identifier.
        //

        private int Id {
            get {
                if (id == 0) {
                    int nid;
                    while ((nid = Interlocked.Increment(ref idSeed)) == 0 ||
                            nid == NOTIFICATION_EVENT_ID) {
                        ;
                    }
                    Interlocked.CompareExchange(ref id, nid, 0);
                }
                return id;
            }
        }

        /*++
         * 
         * Virtual methods that support the waitable functionality.
         * 
         --*/

        //
        // Returns true if the waitable's state allows an immediate acquire.
        //

        internal abstract bool _AllowsAcquire { get; }

        //
        // Tries the waitable acquire operation.
        //

        internal abstract bool _TryAcquire();
        
        //
        // Executes the release semantics associated to the Signal operation.
        //

        internal virtual bool _Release() {
            return false;
        }

        //
        // Executes the prologue for the WaitAny method.
        //

        internal abstract WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock hint, ref int sc);

        //
        // Executes the prologue for the WaitAll method.
        //

        internal abstract WaitBlock _WaitAllPrologue(StParker pk,
                                                     ref WaitBlock hint, ref int sc);

        //
        // Excutes the epilogue for WaitOne and WaitAny methods.
        //

        internal virtual void _WaitEpilogue() { }

        //
        // Undoes a previous acquire operation.
        //

        internal virtual void _UndoAcquire() { }

        //
        // Cancels an acquire attempt.
        //

        internal abstract void _CancelAcquire(WaitBlock wb, WaitBlock hint);

        //
        // Returns the expection that must be thrown when the signal
        // operation fails.
        //

        internal virtual Exception _SignalException {
            get { return new InvalidOperationException(); }
        }

        //
        // Executes the release semantics associated to the
        // Signal operation.
        //

        public virtual bool Signal() { 
            if (!_Release()) {
                Exception sex;
                if ((sex = _SignalException) != null) {
                    throw sex;
                }
                return false;
            }
            return true;
        }

        //
        // Waits until the synchronizer allows the acquire, activating
        // the specified cancellers.
        //

        public bool WaitOne(StCancelArgs cargs) {
            if (_TryAcquire()) {
                return true;
            }

            if (cargs.Timeout == 0) {
                return false;
            }

            var pk = new StParker();
            WaitBlock hint = null;
            int sc = 0;
            WaitBlock wb;
            if ((wb = _WaitAnyPrologue(pk, StParkStatus.Success, ref hint, ref sc)) == null) {
                return true;
            }

            int ws = pk.Park(sc, cargs);

            if (ws == StParkStatus.Success) {
                _WaitEpilogue();
                return true;
            }

            _CancelAcquire(wb, hint);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Waits unconditionally until the waitable allows the acquire.
        //

        public void WaitOne() {
            WaitOne(StCancelArgs.None);
        }

        //
        // Tries to acquire the waitable. Does not block the calling thread.
        //

        public bool TryWaitOne() {
            return _TryAcquire();
        }

        /// <summary>
        /// Waits until one of the specified waitables can be acquired,
        /// activating the specified cancellers. 
        /// </summary>
        /// <param name="ws">The array of Waitables.</param>
        /// <param name="cargs">The cancellation arguments.</param>
        /// <returns></returns>
        public static int WaitAny(StWaitable[] ws, StCancelArgs cargs) {
            if (ws == null) {
                throw new ArgumentNullException("ws");
            }

            return WaitAnyInternal(ws, null, cargs);
        }

        /// <summary>
        /// Waits until one of the specified waitables or wait handles can be 
        /// acquired, activating the specified cancellers. 
        /// </summary>
        /// <param name="ws">The array of waitables.</param>
        /// <param name="hs">The array of wait handles.</param>
        /// <param name="cargs">The cancellation arguments.</param>
        /// <returns></returns>
        public static int WaitAny(StWaitable[] ws, WaitHandle[] hs, StCancelArgs cargs) {
            if (ws == null) {
                throw new ArgumentNullException("ws");
            }

            if (hs == null) {
                throw new ArgumentNullException("hs");
            }

            int len = ws.Length;

            for (int i = 0; i < hs.Length; i++) {
                if (hs[i].WaitOne(0)) {
                    return StParkStatus.Success + len + i;
                }
            }

            return WaitAnyInternal(ws, hs, cargs);
        }

        /// <summary>
        /// Waits unconditionally until one of the specified waitables
        /// can be acquired.
        /// </summary>
        /// <param name="ws">The array of waitables.</param>
        /// <returns></returns>
        public static int WaitAny(StWaitable[] ws) {
            return WaitAny(ws, StCancelArgs.None);
        }

        internal static int WaitAnyInternal(StWaitable[] ws, WaitHandle[] hs, StCancelArgs cargs) {
            int len = ws.Length;

            //
            // First, we scan the *ws* array trying to acquire one of the
            // synchronizers.
            //

            for (int i = 0; i < len; i++) {
                if (ws[i]._TryAcquire()) {
                    return StParkStatus.Success + i;
                }
            }

            if (cargs.Timeout == 0) {
                return StParkStatus.Timeout;
            }

            //
            // Create a parker and execute the WaitAny prologue on all
            // waitables. We stop executing prologues as soon as we detect
            // that the acquire operation was accomplished.
            // 
             
            StParker pk = hs != null 
                        ? new StParker(EventBasedParkSpotFactory.Current.
                                       Create(new WaitAnyBehavior(hs, len)))
                        : new StParker(1);
            WaitBlock[] wbs = new WaitBlock[len];
            WaitBlock[] hints = new WaitBlock[len];

            int lv = -1;
            int sc = 0;
            int gsc = 0;
            for (int i = 0; !pk.IsLocked && i < len; i++) {
                StWaitable w = ws[i];

                if ((wbs[i] = w._WaitAnyPrologue(pk, i, ref hints[i], ref sc)) == null) {
                    if (pk.TryLock()) {
                        pk.UnparkSelf(i);
                    } else {
                        w._UndoAcquire();
                    }
                    break;
                }

                //
                // Adjust the global spin count.
                //

                if (gsc < sc) {
                    gsc = sc;
                }
                lv = i;
            }
            
            int wst = pk.Park(gsc, cargs);
            StWaitable acq = wst >= StParkStatus.Success && wst < len ? ws[wst] : null;
   
            //
            // Cancel the acquire attempt on all waitables where we executed 
            // the WaitAny prologue, except the one we acquired.
            //
        
            for (int i = 0; i <= lv; i++) {
                StWaitable w = ws[i];
                if (w != acq) {
                    w._CancelAcquire(wbs[i], hints[i]);
                }
            }

            if (wst >= StParkStatus.Success && wst < len + (hs != null ? hs.Length : 0)) {
                if (acq != null) {
                    acq._WaitEpilogue();
                }
                return wst;
            }

            StCancelArgs.ThrowIfException(wst);
            return StParkStatus.Timeout;
        }

        //
        // Sorts the waitable array by the waitable id and, at the same time,
        // check if all waitables allow an immediate acquire operation.
        //
        // NOTE: The notification events are not sorted, because they don't
        //       have an acquire side-effect. The notification events are 
        //       grouped at the end of the sorted array.
        //

        private static int SortAndCheckAllowAcquire(StWaitable[] ws, StWaitable[] sws, out int nevts) {
            int i;
            StWaitable w;
            bool acqAll = true;
            int len = ws.Length;

            //
            // Find the first waitable that isn't a notification event,
            // in order to start insertion sort.
            //

            nevts = len;
            for (i = 0; i < len; i++) {
                w = ws[i];
                acqAll &= w._AllowsAcquire;

                //
                // If the current waitable is a notification event, insert
                // it at the end of the ordered array; otherwise, insert it
                // on the begin of the array and break the loop.
                //

                if (w.id == NOTIFICATION_EVENT_ID) {
                    sws[--nevts] = w;
                } else {
                    sws[0] = w;
                    break;
                }
            }

            //
            // If all synchronizers are notification events, return.
            //

            if (nevts == 0) {
                return acqAll ? 1 : 0;
            }

            //
            // Sort the remaining synchronizers using the insertion sort
            // algorithm but only with the non-notification event waitables.
            //
            
            int k = 1;
            for (i++; i < len; i++, k++) {
                w = ws[i];
                acqAll &= w._AllowsAcquire;
                if (w.id == NOTIFICATION_EVENT_ID) {
                    sws[--nevts] = w;
                } else {

                    //
                    // Find the insertion position for *w*.
                    //

                    sws[k] = w;
                    int j = k - 1;
                    while (j >= 0 && sws[j].Id > w.Id) {
                        sws[j + 1] = sws[j];
                        j--;
                    }

                    //
                    // Insert at j+1 position.
                    //

                    sws[j + 1] = w;

                    //
                    // Check for duplicates.
                    //

                    if (sws[k - 1] == sws[k]) {
                        return -1;
                    }
                }
            }
            return acqAll ? 1 : 0;
        }

        private static WaitHandle[] Sort(WaitHandle[] hs) {
            int i, jj;
            int len = hs.Length;
            WaitHandle[] shs = new WaitHandle[len];

            //
            // Find the first waitable that isn't a notification event
            // in order to start the insertion sort.
            //

            jj = len;
            for (i = 0; i < len; i++) {
                WaitHandle h = hs[i];

                //
                // If the current waitable is a notification event, insert
                // it at the end of the ordered array; otherwise, insert it
                // in the begining of the array and break the loop.
                //
                
                if (h is ManualResetEvent) {
                    shs[--jj] = h;
                } else {
                    shs[0] = h;
                    break;
                }
            }

            //
            // If all synchronizers are notification events, return.
            //

            if (i == len) {
                return shs;
            }

            //
            // Sort the remaining synchronizers using the insertion sort
            // algorithm but only with the non-notification event waitables.
            //
            
            int k = 1;
            for (i++; i < len; i++, k++) {
                WaitHandle h = hs[i];
                if (h is ManualResetEvent) {
                    shs[--jj] = h;
                } else {

                    //
                    // Find the insertion position for *h*.
                    //

                    shs[k] = h;
                    int j = k - 1;
                    while (j >= 0 && shs[j].GetHashCode() > h.GetHashCode()) {
                        shs[j + 1] = shs[j];
                        j--;
                    }

                    //
                    // Insert at j+1 position.
                    //

                    shs[j + 1] = h;

                    //
                    // Check for duplicates.
                    //

                    if (shs[k - 1] == shs[k]) {
                        return null;
                    }
                }
            }
            return shs;
        }

        /// <summary>
        /// Waits until all of the specified waitables can be acquired,
        /// activating the specified cancellers.
        /// </summary>
        /// <param name="ws"></param>
        /// <param name="cargs"></param>
        /// <returns></returns>
        public static bool WaitAll(StWaitable[] ws, StCancelArgs cargs) {
            return WaitAllInternal(ws, null, cargs);
        }
        
        /// <summary>
        /// Waits until all of the specified waitables or wait handles can be 
        /// acquired, activating the specified cancellers.
        /// </summary>
        /// <param name="ws"></param>
        /// <param name="hs"></param>
        /// <param name="cargs"></param>
        /// <returns></returns>
        public static bool WaitAll(StWaitable[] ws, WaitHandle[] hs, StCancelArgs cargs) {
            if (hs == null) {
                throw new ArgumentNullException("hs");
            }

            if (cargs.Alerter != null) {
                throw new ArgumentException("Cancellation through the alerter mechanism is not supported", "cargs");
            }

            return WaitAllInternal(ws, hs, cargs);
        }

        internal static bool WaitAllInternal(StWaitable[] ws, WaitHandle[] hs, StCancelArgs cargs) {
            if (ws == null) {
                throw new ArgumentNullException("ws");
            }

            int nevts;
            int len = ws.Length;
            StWaitable[] sws = new StWaitable[len];
            WaitHandle[] shs = null;

            int waitHint = SortAndCheckAllowAcquire(ws, sws, out nevts);
            
            if (waitHint < 0) {
                throw new ArgumentException("There are duplicate waitables", "ws");
            }

            if (hs != null) { 
                shs = Sort(hs);
                if (shs == null) {
                    throw new ArgumentException("There are duplicate wait handles", "hs");
                }
            }

            if (waitHint != 0 && shs != null && !WaitHandle.WaitAll(shs, 0)) {
                waitHint = 0;
            }

            //
            // Return success if all synchronizers are notification events and are set.
            //

            if (waitHint != 0 && nevts == 0) {
                return true;
            }

            if (waitHint == 0 && cargs.Timeout == 0) {
                return false;
            }

            //
            // If a timeout was specified, get the current time in order
            // to adjust the timeout value later, if we re-wait.
            //

            int lastTime = (cargs.Timeout != Timeout.Infinite) ? Environment.TickCount : 0;
            WaitBlock[] wbs = null;
            WaitBlock[] hints = null;
            do {
                if (waitHint == 0) {

                    //
                    // Create the wait block arrays if this is the first time
                    // that we execute the acquire-all prologue.
                    //
                    
                    if (wbs == null) {
                        wbs = new WaitBlock[len];
                        hints = new WaitBlock[len];
                    }

                    //
                    // Create a parker for cooperative release, specifying as many
                    // releasers as the number of waitables. The parker because is
                    // not reused because other threads may have references to it.
                    //

                    StParker pk = shs != null 
                                ? new StParker(len, EventBasedParkSpotFactory.Current.
                                                    Create(new WaitAllBehavior(shs)))
                                : new StParker(len);

                    int gsc = 1;
                    int sc = 0;
                    for (int i = 0; i < len; i++) {
                        if ((wbs[i] = sws[i]._WaitAllPrologue(pk, ref hints[i], ref sc)) == null) {
                            if (pk.TryLock()) {
                                pk.UnparkSelf(StParkStatus.StateChange);
                            }
                        } else if (gsc != 0) {
                            if (sc == 0) {
                                gsc = 0;
                            } else if (sc > gsc) {
                                gsc = sc;
                            }
                        }
                    }
                    
                    int wst = pk.Park(gsc, cargs);

                    //
                    // If the wait was cancelled due to timeout, alert or interrupt,
                    // cancel the acquire attempt on all waitables where we actually
                    // inserted wait blocks.
                    //

                    if (wst != StParkStatus.StateChange) {
                        for (int i = 0; i < len; i++) {
                            WaitBlock wb = wbs[i];
                            if (wb != null) {
                                sws[i]._CancelAcquire(wb, hints[i]);
                            }
                        }
                        
                        StCancelArgs.ThrowIfException(wst);
                        return false;
                    }
                }
            
                //
                // All waitables where we inserted wait blocks seem to allow 
                // an immediate acquire operation; so, try to acquire all of
                // them that are not notification events.
                //

                int idx;
                for (idx = 0; idx < nevts; idx++) {
                    if (!sws[idx]._TryAcquire()) {
                        break;
                    }
                }

                //
                // If all synchronizers were acquired, return success.
                //

                if (idx == nevts) {
                    return true;
                }

                //
                // We failed to acquire all waitables, so undo the acquires
                // that we did above.
                //

                while (--idx >= 0) {
                    sws[idx]._UndoAcquire();
                }

                if (shs != null) {
                    for (int i = 0; i < shs.Length; i++) {
                        shs[i].UndoAcquire();
                    }
                }

                //
                // If a timeout was specified, adjust the timeout value
                // that will be used on the next wait.
                //

                if (!cargs.AdjustTimeout(ref lastTime)) {
                    return false;
                }

                waitHint = 0;
            } while (true);
        }

        //
        // WaitOne unconditionally until all the specified waitables
        // allow the acquire.
        //
    
        public static void WaitAll(StWaitable[] ws) {
            WaitAll(ws, StCancelArgs.None);
        }
 
        //
        // Signals a waitable and waits on another as an atomic
        // operation, activating the specified cancellers.
        //

        public static bool SignalAndWait(StWaitable tos, StWaitable tow, StCancelArgs cargs) {
    
            //
            // Create a parker to execute the WaitAny prologue on the
            // *tow* waitable.
            //

            StParker pk = new StParker();
            WaitBlock hint = null;
            int sc = 0;
            WaitBlock wb = tow._WaitAnyPrologue(pk, StParkStatus.Success, ref hint, ref sc);

            //
            // Signal the *tos* waitable.
            //

            if (!tos._Release()) {

                //
                // The signal operation failed. So, try to cancel the parker and,
                // if successful, cancel the acquire attempt; otherwise, wait until 
                // the thread is unparked and, then, undo the acquire.
                //

                if (pk.TryCancel()) {
                    tow._CancelAcquire(wb, hint);
                } else {
                    pk.Park();
                    tow._UndoAcquire();
                }

                //
                // Report the failure appropriately.
                //

                throw tos._SignalException;
            }

            //
            // Park the current thread, activating the specified cancellers
            // and spinning if appropriate.
            //

            int ws = pk.Park(sc, cargs);

            //
            // If we acquired, execute the WaitOne epilogue and return success.
            //

            if (ws == StParkStatus.Success) {
                tow._WaitEpilogue();
                return true;
            }
    
            //
            // The acquire operation was cancelled; so, cancel the acquire
            // attempt and report the failure appropriately.
            //

            tow._CancelAcquire(wb, hint);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Signals a waitable and waits unconditionally on another
        // as an atomic operation.
        //

        public static void SignalAndWait(StWaitable tos, StWaitable tow) {
            SignalAndWait(tos, tow, StCancelArgs.None);
        }

        //
        // Register a wait with the waitable synchronizer.
        //

        public StRegisteredWait RegisterWait(WaitOrTimerCallback callback, object state,
                                             int timeout, bool executeOnce) {
            return new StRegisteredWait(this,  callback, state, timeout, executeOnce);
        }
    }
}
