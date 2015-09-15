using System; // IntPtr.Size
using System.Collections.Generic; // Dictionary
using System.Threading; // AutoResetEvent
using CodePlex.TfsLibrary.RepositoryWebSvc;
using SvnBridge.SourceControl;
using SvnBridge.Utility; // Helper.DebugUsefulBreakpointLocation()

namespace SvnBridge.Infrastructure
{
    public sealed class MonitoredCommBaseExceptionCancel : Exception
    {
    }

    public sealed class MonitoredCommBaseExceptionTimeout : Exception
    {
    }

    /// <summary>
    /// Single, central thread communication class
    /// which enables to properly atomically wait (block)
    /// on a *single* point
    /// which may be notified for *several* different reasons.
    /// Chose to implement things this way
    /// since C# offers such functionality in a way (via Monitors)
    /// which somewhat differs from usual/known POSIX condition variable handling
    /// (i.e. a global select loop for properly waiting on multiple conditions,
    /// with properly atomic condition variable 3-way-handshake handling).
    /// http://www.java2s.com/Tutorial/CSharp/0420__Thread/UselockandMonitortocoordinateProducerandConsumer.htm
    /// says
    ///   "When selecting an object on which to synchronize,
    ///   you should lock only on private or internal objects.
    ///   Locking on external objects might result in deadlocks,
    ///   because unrelated code could choose the same objects to lock on
    ///   for different purposes."
    /// </summary>
    public class MonitoredCommBase
    {
        private bool cancelOperation /* = false */;
        private bool ignoreCancel /* = false */;

        public MonitoredCommBase()
        {
        }

        /// <summary>
        /// Needs to own lock(), else SynchronizationLockException...
        /// </summary>
        protected void Pulse()
        {
            // No need to have Monitor.IsEntered() check here
            // (.Wait throws SynchronizationLockException if not locked).
            Monitor.Pulse(this);
        }

        /// <summary>
        /// Needs to own lock(), else SynchronizationLockException...
        /// Will throw exceptions on cancel or timeout.
        /// </summary>
        protected void Wait(
            TimeSpan spanTimeout)
        {
            bool isWaitSuccess = false;

            CheckCancel();
            // No need to have Monitor.IsEntered() check here
            // (.Wait throws SynchronizationLockException if not locked).
            // IMPORTANT NOTE (for those who don't know it):
            //   while .Wait()ing the lock will be *cleanly atomically unlocked*.
            isWaitSuccess = Monitor.Wait(
                this,
                spanTimeout);

            // Hrmm... probably rather common execution order problem:
            // for the case of encountering timeout,
            // are we supposed to check for cancel first (i.e. cancel exception comes first),
            // or throw timeout exception first?
            CheckCancel();

            // Unfortunately there likely is no
            // exception-based variant of Monitor.Wait() -
            // otherwise we could have nicely clean error-path-only handling
            // (catch its timeout exception
            // and convert to our outer-layer exception type).
            if (!(isWaitSuccess))
            {
                Helper.DebugUsefulBreakpointLocation();
                throw new MonitoredCommBaseExceptionTimeout();
            }
        }

        public void CheckCancel()
        {
            lock (this)
            {
                CheckCancel_i();
            }
        }

        private void CheckCancel_i()
        {
            bool needCancel = (cancelOperation && !ignoreCancel);
            bool canContinue = !(needCancel);
            if (!(canContinue))
            {
                DoCancel_i();
            }
        }

        private static void DoCancel_i()
        {
            Helper.DebugUsefulBreakpointLocation();
            throw new MonitoredCommBaseExceptionCancel();
        }

        public void Cancel()
        {
            lock (this)
            {
                Cancel_i();
            }
        }

        private void Cancel_i()
        {
            cancelOperation = true;
            Pulse();
        }

        /// <summary>
        /// To be called once cancelling ought to be ignored
        /// (e.g. once in shutdown already).
        /// </summary>
        protected void SetCancelIgnored()
        {
            ignoreCancel = true;
        }
    }

    internal sealed class MonitoredCommAsyncItemLoader : MonitoredCommBase
    {
        // Need a separate dictionary
        // (we cannot submit an IAsyncResult-derived class
        // since this IAsyncResult is generated by foreign layers!).
        private readonly Dictionary<IAsyncResult, object> dictAsync;
        private readonly int requestsInflightMax;
        private readonly DateTime timeUtcExpireProduction;

        /// NOTE Monitor handling:
        /// lock() (of this class's *scope*!)
        /// should always (/usually?) be enacted
        /// right where entering this class,
        /// i.e. fully symmetrically in all *public* methods.
        public MonitoredCommAsyncItemLoader(
            DateTime timeUtcExpireProduction,
            int requestsInflightMax)
        {
            this.dictAsync = new Dictionary<IAsyncResult, object>(requestsInflightMax);
            this.requestsInflightMax = requestsInflightMax;
            this.timeUtcExpireProduction = timeUtcExpireProduction;
        }

        private TimeSpan GetSpanExpire()
        {
            return timeUtcExpireProduction - DateTime.UtcNow;
        }

        public void RequestSlotOccupy(
            IAsyncResult ar,
            object obj)
        {
            lock (this)
            {
                RequestSlotOccupy_i(
                    ar,
                    obj);
            }
        }

        private void RequestSlotOccupy_i(
            IAsyncResult ar,
            object obj)
        {
            // *First* queue the item
            // (ensure item availability *prior* to async callback happening!!)...
            dictAsync[ar] = obj;

            // ...*then* throttle in case we managed to hit the request limit.
            ThrottleSlots();
        }

        private void ThrottleSlots()
        {
            for (;;)
            {
                bool haveIdleSlots = (dictAsync.Count < requestsInflightMax);
                // IMPORTANT: definitely remember to do an *initial* status check
                // directly prior to first wait.
                if (haveIdleSlots)
                {
                    break;
                }

                WaitEvent();
            }
        }

        private void WaitEvent()
        {
            try
            {
                Wait(
                    GetSpanExpire());
            }
            catch (MonitoredCommBaseExceptionTimeout e)
            {
                ReportErrorItemDataProductionTimeout(
                    e);
            }
        }

        public object RequestSlotGet(
            IAsyncResult ar)
        {
            object obj = null;
            lock (this)
            {
                obj = dictAsync[ar];
            }
            return obj;
        }

        public void RequestSlotRelease(
            IAsyncResult ar)
        {
            lock (this)
            {
                RequestSlotRelease_i(
                    ar);
            }
        }

        private void RequestSlotRelease_i(
            IAsyncResult ar)
        {
            try
            {
                RequestSlotRelease_Do(
                    ar);
            }
            // I guess we should signal activity unconditionally
            // (both when successful and when not).
            finally
            {
                Pulse();
            }
        }

        private void RequestSlotRelease_Do(
            IAsyncResult ar)
        {
            // Since Dictionary.Remove()
            // is NOT documented to throw exception,
            // we'll better manually (i.e. unfortunately in non-error path)
            // additionally check validity up-front.
            bool haveSlotsRemaining = (0 < dictAsync.Count);
            if (haveSlotsRemaining)
            {
                dictAsync.Remove(
                    ar);
            }
            else
            {
                ReportErrorNoRequestSlotsRemaining();
            }
        }

        public void WaitFinished()
        {
            lock (this)
            {
                WaitFinished_i();
            }
        }

        private void WaitFinished_i()
        {
            SetCancelIgnored();
            WaitFinishedImpl();
        }

        private void WaitFinishedImpl()
        {
            WaitFinishedImpl_NoSlotsRemaining();
        }

        private void WaitFinishedImpl_NoSlotsRemaining()
        {
            for (;;)
            {
                bool noSlotsRemaining = (0 >= dictAsync.Count);

                if (noSlotsRemaining)
                {
                    break;
                }

                // USEFUL BREAKPOINT LOCATION
                // (determine pending async-I/O items)

                WaitEvent();
            }
        }

        private static void ReportErrorItemDataProductionTimeout(
            Exception inner)
        {
            Helper.DebugUsefulBreakpointLocation();
            throw new TimeoutException("Timeout while waiting for filesystem item data production to be completed", inner);
        }

        private static void ReportErrorNoRequestSlotsRemaining()
        {
            Helper.DebugUsefulBreakpointLocation();
            throw new InvalidOperationException("no request slots remaining!?");
        }
    }

    internal sealed class ItemDataDownloadWaiter
    {
        private readonly ItemMetaData item;
        private readonly WaitHandle[] finishedOneArray;

        public ItemDataDownloadWaiter(
            ItemMetaData item,
            WaitHandle finishedOne)
        {
            this.item = item;
            this.finishedOneArray = new WaitHandle[] { finishedOne };
        }

        public void Wait(
            TimeSpan spanTimeout)
        {
            WaitLoop(
                spanTimeout);
        }

        private void WaitLoop(
            TimeSpan spanTimeout)
        {
            DateTime timeUtcWaitLoaded_Start = DateTime.UtcNow; // Calculate ASAP (to determine timeout via precise right-upon-start timestamp)
            DateTime timeUtcWaitLoaded_Expire = DateTime.MinValue;
            TimeSpan spanTimeoutRemain = spanTimeout;

            // Since the event handle currently is loader-global (and probably will remain,
            // since that ought to be more efficient than per-item handles horror),
            // it will be used to signal *any* progress.
            // Thus we need to keep iterating until in fact *our* item is loaded.
            // And since we need to do that, update a timeout value
            // which will be reliably determined from actual current timestamp.

            for (;;)
            {
                bool isTimeout;
                DoWait(
                    spanTimeoutRemain,
                    out isTimeout);
                if (isTimeout)
                {
                    break;
                }

                // One reason for .DataLoaded ending up false
                // even after having waited for success
                // is that loader is now loading multiple items in parallel
                // (via truly asynchronous request handling),
                // thus it may happen that
                // while item production and consumption side
                // are doing same-order recursion,
                // one item has been loaded successfully faster (perhaps less data?)
                // and thus is ready earlier - but this is not the item yet
                // that the consumer side here is currently waiting for!!
                if (item.DataLoaded)
                {
                    break;
                }

                // Performance: nice trick: do expensive calculation of expiration stamp
                // only *after* the first wait has already been done :)
                bool expire_needs_init = (DateTime.MinValue == timeUtcWaitLoaded_Expire);
                if (expire_needs_init)
                {
                    timeUtcWaitLoaded_Expire = timeUtcWaitLoaded_Start + spanTimeout;
                }

                // Performance: implement grabbing current timestamp ALAP, to have strict timeout-side handling.
                DateTime timeUtcNow = DateTime.UtcNow; // debug helper

                // Make sure to have the timeout value variable updated for next round above:
                // And make sure to have handling be focussed on a precise final timepoint
                // to have a precisely bounded timeout endpoint
                // (i.e., avoid annoying accumulation of added-up multi-wait scheduling delays imprecision).
                spanTimeoutRemain = timeUtcWaitLoaded_Expire - timeUtcNow;
            }
        }

        private void DoWait(
            TimeSpan spanTimeout,
            out bool isTimeout)
        {
            isTimeout = false;

            // IMPORTANT: definitely remember to do an *initial* status check
            // directly prior to first wait.
            if (item.DataLoaded)
            {
                return;
            }

            // FIXME!! race window *here*:
            // if producer happens to be doing [set .DataLoaded true and signal event] *right here*,
            // then prior .DataLoaded false check will have failed
            // and we're about to wait on an actually successful load
            // (and having missed the signal event).
            // The properly atomically scoped (read: non-racy) solution likely would be
            // to move .DataLoaded evaluation inside a Monitor scope
            // (and below wait activity would then unlock the Monitor),
            // but since finishedOneArray is currently managed separately from a Monitor scope
            // properly handshaked unlocked-waiting might be not doable ATM.

            // To observe item loader's async download progress,
            // see method which serves async callback
            // (TfsLibrary.DownloadBytesReadState.ReadCallback()).
            // Cannot use .WaitOne() since that one does not signal .WaitTimeout (has bool result).
            int idxEvent = WaitHandle.WaitAny(finishedOneArray, spanTimeout);
            isTimeout = (WaitHandle.WaitTimeout == idxEvent);
        }
    }

    public /* no "sealed" here (class subsequently derived by Tests) */ class AsyncItemLoader
    {
        private readonly FolderMetaData folderInfo;
        private readonly TFSSourceControlProvider sourceControlProvider;
        private readonly long cacheTotalSizeLimit;

        // Have a precisely bounded timeout endpoint to compare against:
        private readonly DateTime timeUtcExpireProduction;

        private readonly MonitoredCommAsyncItemLoader monitoredComm;

        private readonly AsyncCallback callback;

        private readonly AutoResetEvent finishedOne;

        private readonly AutoResetEvent crawlerEvent;
        private readonly WaitHandle[] crawlerEventArray;

        private readonly TimeSpan spanTimeoutAwaitAnyConsumptionActivity;

        private readonly TimeSpan spanTimeoutTryWaitConsumptionStep;

        public AsyncItemLoader(FolderMetaData folderInfo, TFSSourceControlProvider sourceControlProvider, long cacheTotalSizeLimit)
        {
            this.folderInfo = folderInfo;
            this.sourceControlProvider = sourceControlProvider;
            this.cacheTotalSizeLimit = cacheTotalSizeLimit;

            TimeSpan spanExpireProduction = TimeoutExpireProduction;
            this.timeUtcExpireProduction = DateTime.UtcNow + spanExpireProduction;

            var tfsRequestsPendingMax = DetermineTfsRequestsPendingMax();
            this.monitoredComm = new MonitoredCommAsyncItemLoader(
                timeUtcExpireProduction,
                tfsRequestsPendingMax);

            this.callback = new AsyncCallback(
                OnItemDataDownloadEnded);

            this.finishedOne = new AutoResetEvent(false);

            // Performance: do allocation of event array on init rather than per-use.
            // And keep specific member for handle itself, too,
            // to enable direct (non-dereferenced) fast access.
            this.crawlerEvent = new AutoResetEvent(false);
            this.crawlerEventArray = new WaitHandle[] { crawlerEvent };

            this.spanTimeoutAwaitAnyConsumptionActivity = TimeoutAwaitAnyConsumptionActivity;

            this.spanTimeoutTryWaitConsumptionStep = DefineTimeoutTryWaitConsumptionStep();
        }

        /// <summary>
        /// Determines the maximum number (limit) of pending TFS requests
        /// to be submitted.
        /// </summary>
        /// <remarks>
        /// This number may easily turn out dangerously high
        /// (usually this limit should be around 3, 4 requests).
        /// Such values have been experimentally determined
        /// to be a useful value when judging against:
        /// - other ongoing serialized (read: blocking) activities
        /// - server-side capabilities
        ///   (memory resources may be very limited,
        ///   and resource use prediction is difficult,
        ///   number of cores might be relevant and limited)
        ///
        /// Detailed reasons for keeping it very low:
        /// - the more slots we queue up in advance,
        ///   the bigger our mismatch
        ///   between "potentially largest download size"
        ///   and "calculated size as existing in our folder hierarchy"
        ///   (--> high risk of getting dangerously close
        ///   to exceeding memory resources)
        /// - TfsLibrary download handling (DownloadBytesReadState)
        ///   is awfully weak, e.g. it does NOT support
        ///   properly partially streamed chained forwarding
        ///   via *fully end-to-end* pass-through via read callback,
        ///   but rather (and completely stupidly)
        ///   aggregates the entire (potentially multi-TB!!) download size
        ///   in memory - talk about SPOF...
        ///
        /// The article
        /// ""Concurrent" users"
        ///   http://blogs.msdn.com/b/bharry/archive/2005/12/03/499791.aspx
        /// contains the following "interesting" statement:
        /// "We found that, in our data, this number is 1 request per second per concurrent user."
        /// which seems to rather confirm
        /// that there is not much to be too excited about
        /// when it comes to request performance
        /// of Microsoft-eco-system-specific server software...
        /// </remarks>
        private static int DetermineTfsRequestsPendingMax()
        {
            int tfsRequestsPendingMax = Configuration.TfsRequestsPendingMax;

            if (Helper.NeedAvoidLongLivedMemoryFragmentation)
            {
                // Choose the minimum value
                // which has strongly reduced memory activity
                // but still allows for enjoying some benefits of parallelism.
                const int tfsRequestsPendingReducedLimit = 3;
                if (tfsRequestsPendingMax > tfsRequestsPendingReducedLimit)
                {
                    tfsRequestsPendingMax = tfsRequestsPendingReducedLimit;
                }
            }

            return tfsRequestsPendingMax;
        }

        public void Start()
        {
            try
            {
                ReadItemsInFolder(folderInfo);
            }
            catch (MonitoredCommBaseExceptionCancel)
            {
                // Nothing to be done other than cleanly bailing out
            }
            finally
            {
                // FIXME: this does not fully cleanly restore things I guess
                // (think of cancelling pending async activity once a Cancel got received...).
                WaitFinished();
            }
        }

        public virtual void Cancel()
        {
            monitoredComm.Cancel();
            NotifyCrawler(); // FIXME: ugly workaround to also notify the *other* wait parts...
        }

        private void NotifyCrawler()
        {
            crawlerEvent.Set();
        }

        /// <summary>
        /// To be called wherever a cancel request
        /// may (need to) be properly/timely served.
        /// Invocation should ideally be tending towards *inner* layers
        // (i.e., right before/after an expensive/long-standing calculation/wait).
        /// </summary>
        private void CheckCancel()
        {
            monitoredComm.CheckCancel();
        }

        private void WaitFinished()
        {
            monitoredComm.WaitFinished();
        }

        /// <remarks>
        /// http://blogs.msdn.com/b/granth/archive/2013/02/13/tfs-load-balancers-idle-timeout-settings-and-tcp-keep-alives.aspx
        /// says
        /// ". Between the TFS ASP.NET Application and SQL Server,
        ///    there is a maximum execution timeout of 3600 seconds (1 hour)
        ///  . In IIS/ASP.NET there is a maximum request timeout of 3600 seconds
        ///    (it's no coincidence that it matches)"
        /// Thus, choose a global production-side timeout setting
        /// that is at least as big as these.
        /// </remarks>
        private static TimeSpan TimeoutExpireProduction
        {
            get
            {
                return TimeSpan.FromHours(
                    4);
            }
        }

        private void ReadItemsInFolder(FolderMetaData folder)
        {
            CheckCancel();

            foreach (ItemMetaData item in folder.Items)
            {
                if (item.ItemType == ItemType.Folder)
                {
                    ReadItemsInFolder((FolderMetaData) item);
                }
                else if (!(item is DeleteMetaData))
                {
                    // Before reading further data, verify total pending size:

                    // Do the below-size-limit check within the scope and right before
                    // the single place which actually is going to read more data.
                    WaitForUnusedItemLoadBufferCapacity();

                    SubmitOne(
                        item);
                }
            }
        }

        private void WaitForUnusedItemLoadBufferCapacity()
        {
            // Ensure that this crawler resource will bail out at least eventually
            // in case of a problem (e.g. missing consumer-side fetching).
            // And make sure to keep calculating this expire time *locally*,
            // i.e. for every use of this handler!
            DateTime timeUtcStartWait = DateTime.UtcNow;
            // Have a precisely bounded timeout endpoint to compare against:
            DateTime timeUtcExpireAwaitAnyConsumptionActivity = timeUtcStartWait + spanTimeoutAwaitAnyConsumptionActivity;
            for (; ; )
            {
                var totalLoadedItemsSize = CalculateLoadedItemsSize(folderInfo);
                bool haveUnusedItemLoadBufferCapacity = HaveUnusedItemLoadBufferCapacity(totalLoadedItemsSize);
                if (haveUnusedItemLoadBufferCapacity)
                {
                    break;
                }

                DateTime timeUtcNow = DateTime.UtcNow; // debug helper
                bool isWaitExceeded = (timeUtcNow >= timeUtcExpireAwaitAnyConsumptionActivity);
                bool haveTimeRemain = !(isWaitExceeded);
                if (!(haveTimeRemain))
                {
                    ReportErrorItemDataConsumptionTimeout();
                }

                // Do some waiting until hopefully parts of totalLoadedItemsSize
                // got consumed (by consumer side, obviously).
                bool isWaitSuccess = WaitNotify(
                    spanTimeoutTryWaitConsumptionStep);
            }
        }

        private bool HaveUnusedItemLoadBufferCapacity(long totalLoadedItemsSize)
        {
            bool haveUnusedItemLoadBufferCapacity = false;

            var unusedItemLoadBufferCapacity = (CacheTotalSizeLimit - totalLoadedItemsSize);

            if (0 < unusedItemLoadBufferCapacity)
            {
                haveUnusedItemLoadBufferCapacity = true;
            }

            return haveUnusedItemLoadBufferCapacity;
        }

        /// <remarks>
        /// A timeout of 4 hours ought to be more than enough
        /// to expect a client
        /// (which simply is waiting for us to produce things,
        /// as opposed to us having to go through *hugely* complex
        /// TFS <-> SVN conversion processes)
        /// to have fetched data.
        ///
        /// Well, hmm, but OTOH in pathological cases
        /// even this timeout *will* get exceeded
        /// since some very large requests (>10M total) may happen
        /// where the consumer is waiting for the last parts -
        /// crawler will hit limit, and consumer will never get its last file served.
        /// We will have to drastically rework things to handle file crawling
        /// in a more robust way.
        /// </remarks>
        private static TimeSpan TimeoutAwaitAnyConsumptionActivity
        {
            get { return TimeSpan.FromHours(4); }
        }

        private void SubmitOne(
            ItemMetaData item)
        {
            SubmitOne_Do(
                item);
        }

        private void SubmitOne_Do(
            ItemMetaData item)
        {
            // Warning: potential race window!
            // Since BeginReadFile() might happen to fully process things instantly (who knows...),
            // this might result in the callback getting called prior to us
            // even having enqueued the externally generated IAsyncResult,
            // thus the callback's lookup would come up empty.
            // Thus we need to extend scope of the lookup table's lock
            // *prior* to even starting the operation.
            lock (monitoredComm)
            {
                SubmitOne_Do_i(
                    item);
            }
        }

        private void SubmitOne_Do_i(
            ItemMetaData item)
        {
            IAsyncResult ar = BeginDownloadItemData(
                item);
            monitoredComm.RequestSlotOccupy(ar, item);
        }

        private bool WaitNotify(
            TimeSpan spanTimeout)
        {
            bool isWaitSuccess = false;

            CheckCancel();

            int idxEvent = WaitHandle.WaitAny(crawlerEventArray, spanTimeout);
            bool isTimeout = (WaitHandle.WaitTimeout == idxEvent);
            isWaitSuccess = !(isTimeout);

            CheckCancel();

            return isWaitSuccess;
        }

        private void OnItemDataDownloadEnded(
            IAsyncResult ar)
        {
            // Warning: any exceptions
            // generated by operations within this *callback* here
            // will end up being handled
            // within the *callback-invoking outer* part (toolkit)!
            // To conveniently debug catching exception cases,
            // use the usual MSVS Exceptions dialog (Ctrl-Alt-E).
            ItemMetaData item = null;
            try
            {
                byte[] data = null;

                TryDetermineItemAndData(
                    ar,
                    out item,
                    out data);

                ItemContentDataAdopt(
                    item,
                    data);
            }
            catch
            {
                Helper.DebugUsefulBreakpointLocation();

                throw;
            }
            finally
            {
                // Ultimately, make damn sure to *always* notify
                // the infinitely waiting consumer,
                // irrespective of whether there was failure or not
                // (well, at least notify the moment that we decided to skip
                // doing (implementing??) [further] retries on failure).
                // (FIXME!!: should be implementing a clean retry [3 times?]
                // mechanism - however that is difficult since retry
                // counts ought to be managed somewhere near per-item-instance areas,
                // however we don't have that infrastructure currently;
                // ok we could change the mapping between ar and item
                // to map between ar and an item context struct).
                NotifyConsumer_ItemProcessingEnded(
                    item);
            }
        }

        private void TryDetermineItemAndData(
            IAsyncResult ar,
            out ItemMetaData item,
            out byte[] data)
        {
            item = null;

            // Side note: decide to do *symmetric* operation:
            // Begin()
            //   Add()
            //   Remove()
            // End()
            lock (monitoredComm)
            {
                try
                {
                    try
                    {
                        item = (ItemMetaData)monitoredComm.RequestSlotGet(
                            ar);
                    }
                    finally
                    {
                        try
                        {
                            data = EndDownloadItemData(
                                ar);
                        }
                        catch
                        {
                            //System.Diagnostics.Debugger.Launch();
                            Helper.DebugUsefulBreakpointLocation();

                            throw;
                        }
                    }
                }
                finally
                {
                    // Definitely ensure releasing the slot,
                    // and precisely in a reliable "finally"
                    // within the *locked* scope
                    // (at the very end of it).
                    monitoredComm.RequestSlotRelease(
                        ar);
                }
            }
        }

        private IAsyncResult BeginDownloadItemData(
            ItemMetaData item)
        {
            IAsyncResult ar;

            ar = sourceControlProvider.BeginReadFile(
                item,
                callback);

            return ar;
        }

        /// <remarks>
        /// http://stackoverflow.com/a/229584
        /// </remarks>
        private byte[] EndDownloadItemData(
            IAsyncResult ar)
        {
            return sourceControlProvider.EndReadFile(
                ar);
        }

        private static void ItemContentDataAdopt(
            ItemMetaData item,
            byte[] data)
        {
            item.ContentDataAdopt(
                data);
        }

        /// <remarks>
        /// Since we now have proper notification of item consumption
        /// (producer gets notified once an item was fetched by consumer)
        /// and we act on it,
        /// we (usually...) should get properly notified,
        /// thus we can now have a much larger timeout
        /// (and make it drastically large,
        /// to be able to prominently notice implementation failure:
        /// e.g. that we failed to get notified
        /// on an item-consumption wait -
        /// but don't use an infinite or even overly large timeout
        /// since that would break service quality
        /// on such a "negligible" implementation failure),
        /// to avoid power-management-hampering useless wakeups.
        /// </remarks>
        private static TimeSpan DefineTimeoutTryWaitConsumptionStep()
        {
            //return TimeSpan.FromSeconds(1);
            return TimeSpan.FromMinutes(30);
        }

        /// <summary>
        /// Notifies the consumer side
        /// (well, at least once the time comes
        /// that we do want to let the consumer side
        /// learn of the new [and final] item state)
        /// that processing (i.e., download activities)
        /// of this item ultimately ended
        /// (irrespective of whether successful *or* not).
        /// </summary>
        private void NotifyConsumer_ItemProcessingEnded(ItemMetaData item)
        {
            NotifyConsumer(); // new item available
        }

        private void NotifyConsumer()
        {
            finishedOne.Set();
        }

        /// <summary>
        /// Queries total data size of all items within the entire directory hierarchy
        /// (i.e. file content data that we gathered and that awaits retrieval by client side).
        /// </summary>
        /// <param name="folder">Base folder to calculate the hierarchical data items size of</param>
        /// <returns>Byte count currently occupied by data items below the base folder</returns>
        private long CalculateLoadedItemsSize(FolderMetaData folder)
        {
            long itemsSize = 0;

            CheckCancel();

            foreach (ItemMetaData item in folder.Items)
            {
                if (item.ItemType == ItemType.Folder)
                {
                    itemsSize += CalculateLoadedItemsSize((FolderMetaData) item);
                }
                else if (item.DataLoaded)
                {
                    itemsSize += item.Base64DiffData.Length;
                }
            }

            CheckCancel();

            return itemsSize;
        }

        private long CacheTotalSizeLimit
        {
            get
            {
                return cacheTotalSizeLimit;
            }
        }

        private static void ReportErrorItemDataConsumptionTimeout()
        {
            Helper.DebugUsefulBreakpointLocation();
            throw new TimeoutException("Timeout while waiting for consumption of filesystem item data");
        }

        /// <summary>
        /// Helper for the *consumer*-side thread context,
        /// to allow for reliable waiting and fetching of item data
        /// (after item has achieved "loaded" state).
        /// </summary>
        /// <param name="item">Item whose data we will be waiting for to have finished loading</param>
        /// <param name="spanTimeout">Expiry timeout for waiting for the item's data to become loaded</param>
        /// <param name="base64DiffData">Receives the base64-encoded diff data</param>
        /// <param name="md5Hash">Receives the MD5 hash which had been calculated the moment the data has been stored (ensure end-to-end validation)</param>
        public bool TryRobItemData(
            ItemMetaData item,
            TimeSpan spanTimeout,
            out string base64DiffData,
            out string md5Hash)
        {
            bool gotData = false;

            gotData = WaitForItemLoaded(
                item,
                spanTimeout);

            if (gotData)
            {
                base64DiffData = DoRobItemData(
                    item,
                    out md5Hash);
            }
            else
            {
                base64DiffData = "";
                md5Hash = "";
            }

            return gotData;
        }

        private bool WaitForItemLoaded(
            ItemMetaData item,
            TimeSpan spanTimeout)
        {
            ItemDataDownloadWaiter waiter = new ItemDataDownloadWaiter(
                item,
                finishedOne);

            waiter.Wait(
                spanTimeout);

            return item.DataLoaded;
        }

        private string DoRobItemData(
            ItemMetaData item,
            out string md5Hash)
        {
            string base64DiffData;

            base64DiffData = item.ContentDataRobAsBase64(
                out md5Hash);
            NotifyCrawlerItemConsumed(
                item);

            return base64DiffData;
        }

        /// <remarks>
        /// One item down, 9999 to go...
        /// </remarks>
        private void NotifyCrawlerItemConsumed(
            ItemMetaData item)
        {
            NotifyCrawler();
        }
    }
}
