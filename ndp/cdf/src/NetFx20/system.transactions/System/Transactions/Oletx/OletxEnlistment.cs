using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Threading;
using System.Transactions;
using System.Transactions.Diagnostics;

namespace System.Transactions.Oletx
{
    [Serializable]
    internal class OletxRecoveryInformation
    {
        internal byte[] proxyRecoveryInformation;

        internal OletxRecoveryInformation(
            byte[] proxyRecoveryInformation
            )
        {
            this.proxyRecoveryInformation = proxyRecoveryInformation;
        }
    }
    
    class OletxEnlistment : OletxBaseEnlistment,
        IPromotedEnlistment
    {
        internal enum OletxEnlistmentState
        {
            Active,
            Phase0Preparing,
            Preparing,
            SinglePhaseCommitting,
            Prepared,
            Committing,
            Committed,
            Aborting,
            Aborted,
            InDoubt,
            Done
        }

        IEnlistmentShim enlistmentShim;
        IPhase0EnlistmentShim phase0Shim;
        bool canDoSinglePhase;
        IEnlistmentNotificationInternal iEnlistmentNotification;
        // The information that comes from/goes to the proxy.
        byte[] proxyPrepareInfoByteArray = null;

        OletxEnlistmentState state = OletxEnlistmentState.Active;

        bool isSinglePhase = false;
        Guid transactionGuid = Guid.Empty;

        // We need to keep track of the handle for the phase 1 notifications
        // so that if the enlistment terminates the conversation due for instance
        // to a force rollback the handle can be cleaned up.
        internal IntPtr phase1Handle = IntPtr.Zero;

        // Set to true if we receive an AbortRequest while we still have
        // another notification, like prepare, outstanding.  It indicates that
        // we need to fabricate a rollback to the app after it responds to Prepare.
        bool fabricateRollback = false;

        bool tmWentDown = false;
        bool aborting = false;

        byte[] prepareInfoByteArray;

        internal Guid TransactionIdentifier
        {
            get
            {
                return transactionGuid;
            }
        }
        
        #region Constructor
        
        internal OletxEnlistment(
            bool canDoSinglePhase,
            IEnlistmentNotificationInternal enlistmentNotification,
            Guid transactionGuid,
            EnlistmentOptions enlistmentOptions,
            OletxResourceManager oletxResourceManager,
            OletxTransaction oletxTransaction
            ) : base( oletxResourceManager, oletxTransaction )
        {
            Guid myGuid = Guid.Empty;

            // This will get set later by the creator of this object after it
            // has enlisted with the proxy.
            this.enlistmentShim = null;
            this.phase0Shim = null;

            this.canDoSinglePhase = canDoSinglePhase;
            this.iEnlistmentNotification = enlistmentNotification;
            this.state = OletxEnlistmentState.Active;
            this.transactionGuid = transactionGuid;

            this.proxyPrepareInfoByteArray = null;

            if ( DiagnosticTrace.Information )
            {
                EnlistmentTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentType.Durable,
                    enlistmentOptions
                    );
            }

            // Always do this last incase anything earlier fails.
            AddToEnlistmentTable();
        }


        internal OletxEnlistment(
            IEnlistmentNotificationInternal enlistmentNotification,
            OletxTransactionStatus xactStatus,
            byte[] prepareInfoByteArray,
            OletxResourceManager oletxResourceManager
            ) : base( oletxResourceManager, null )
        {
            Guid myGuid = Guid.Empty;

            // This will get set later by the creator of this object after it
            // has enlisted with the proxy.
            this.enlistmentShim = null;
            this.phase0Shim = null;

            this.canDoSinglePhase = false;
            this.iEnlistmentNotification = enlistmentNotification;
            this.state = OletxEnlistmentState.Active;

            // Do this before we do any tracing because it will affect the trace identifiers that we generate.
            Debug.Assert( ( null != prepareInfoByteArray ),  
                "OletxEnlistment.ctor - null oletxTransaction without a prepareInfoByteArray" );

            int prepareInfoLength = prepareInfoByteArray.Length;
            this.proxyPrepareInfoByteArray = new byte[prepareInfoLength];
            Array.Copy(prepareInfoByteArray, proxyPrepareInfoByteArray, prepareInfoLength);

            byte[] txGuidByteArray = new byte[16];
            Array.Copy(proxyPrepareInfoByteArray, txGuidByteArray, 16);

            this.transactionGuid = new Guid( txGuidByteArray );
            this.transactionGuidString = this.transactionGuid.ToString();
            
            // If this is being created as part of a Reenlist and we already know the
            // outcome, then tell the application.
            switch (xactStatus)
            {
                case OletxTransactionStatus.OLETX_TRANSACTION_STATUS_ABORTED:
                {
                    this.state = OletxEnlistmentState.Aborting;
                    if ( DiagnosticTrace.Verbose )
                    {
                        EnlistmentNotificationCallTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            this.InternalTraceIdentifier,
                            NotificationCall.Rollback
                            );
                    }

                    iEnlistmentNotification.Rollback( this );
                    break;
                }

                case OletxTransactionStatus.OLETX_TRANSACTION_STATUS_COMMITTED:
                {
                    this.state = OletxEnlistmentState.Committing;
                    // We are going to send the notification to the RM.  We need to put the
                    // enlistment on the reenlistPendingList.  We lock the reenlistList because
                    // we have decided that is the lock that protects both lists.  The entry will
                    // be taken off the reenlistPendingList when the enlistment has
                    // EnlistmentDone called on it.  The enlistment will call
                    // RemoveFromReenlistPending.
                    lock ( oletxResourceManager.reenlistList )
                    {
                        oletxResourceManager.reenlistPendingList.Add( this );
                    }

                    if ( DiagnosticTrace.Verbose )
                    {
                        EnlistmentNotificationCallTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            this.InternalTraceIdentifier,
                            NotificationCall.Commit
                            );
                    }

                    iEnlistmentNotification.Commit( this );
                    break;
                }

                case OletxTransactionStatus.OLETX_TRANSACTION_STATUS_PREPARED:
                {
                    this.state = OletxEnlistmentState.Prepared;
                    lock ( oletxResourceManager.reenlistList )
                    {
                        oletxResourceManager.reenlistList.Add( this );
                        oletxResourceManager.StartReenlistThread();
                    }
                    break;
                }

                default:
                {
                    if ( DiagnosticTrace.Critical )
                    {
                        InternalErrorTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            SR.GetString( SR.OletxEnlistmentUnexpectedTransactionStatus )
                            );
                    }

                    throw TransactionException.Create(SR.GetString(SR.TraceSourceOletx), SR.GetString(SR.OletxEnlistmentUnexpectedTransactionStatus), null, this.DistributedTxId);
                }
            }

            if ( DiagnosticTrace.Information )
            {
                EnlistmentTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentType.Durable,
                    EnlistmentOptions.None
                    );
            }

            // Always do this last in case anything prior to this fails.
            AddToEnlistmentTable();
        }
        #endregion
        

        internal IEnlistmentNotificationInternal EnlistmentNotification
        {
            get
            {
                return iEnlistmentNotification;
            }
        }


        internal IEnlistmentShim EnlistmentShim
        {
            get { return this.enlistmentShim; }
            set { this.enlistmentShim = value; }
        }
        

        internal IPhase0EnlistmentShim Phase0EnlistmentShim
        {
            get { return this.phase0Shim; }
            set
            { 
                lock ( this )
                {
                    // If this.aborting is set to true, then we must have already received a
                    // Phase0Request.  This could happen if the transaction aborts after the
                    // enlistment is made, but before we are given the shim.
                    if ( ( null != value ) && ( this.aborting || this.tmWentDown ) )
                    {
                        value.Phase0Done( false );
                    }
                    this.phase0Shim = value; 
                }
            }
        }
        

        internal OletxEnlistmentState State
        {
            get { return state; }
            set { state = value; }
        }

        internal byte[] ProxyPrepareInfoByteArray
        {
            get { return proxyPrepareInfoByteArray; }
        }
        
       
        internal void FinishEnlistment()
        {
            lock ( this )
            {
                // If we don't have a wrappedTransactionEnlistmentAsync, we may
                // need to remove ourselves from the reenlistPendingList in the
                // resource manager.
                if ( null == this.enlistmentShim )
                {
                    oletxResourceManager.RemoveFromReenlistPending( this );
                }
                this.iEnlistmentNotification = null;

                RemoveFromEnlistmentTable();
            }
        }

        internal void TMDownFromInternalRM( OletxTransactionManager oletxTm )
        {
            lock ( this )
            {
                // If we don't have an oletxTransaction or the passed oletxTm matches that of my oletxTransaction, the TM went down.
                if ( ( null == this.oletxTransaction ) || ( oletxTm == this.oletxTransaction.realOletxTransaction.OletxTransactionManagerInstance ) )
                {
                    this.tmWentDown = true;
                }
            }

            return;
        }


        #region ITransactionResourceAsync methods
        
        // ITranactionResourceAsync.PrepareRequest
        public bool PrepareRequest(
            bool singlePhase,
            byte[] prepareInfo
            )
        {
            IEnlistmentShim localEnlistmentShim = null;
            OletxEnlistmentState localState = OletxEnlistmentState.Active;
            IEnlistmentNotificationInternal localEnlistmentNotification = null;
            OletxRecoveryInformation oletxRecoveryInformation = null;
            bool enlistmentDone;

            lock ( this )
            {
                if ( OletxEnlistmentState.Active == state )
                {
                    localState = state = OletxEnlistmentState.Preparing;
                }
                else
                {
                    // We must have done the prepare work in Phase0, so just remember what state we are
                    // in now.
                    localState = state;
                }

                localEnlistmentNotification = iEnlistmentNotification;

                localEnlistmentShim = this.EnlistmentShim;

                this.oletxTransaction.realOletxTransaction.TooLateForEnlistments = true;
            }

            // If we went to Preparing state, send the app
            // a prepare request.
            if ( OletxEnlistmentState.Preparing == localState )
            {
                oletxRecoveryInformation = new OletxRecoveryInformation( prepareInfo );
                this.isSinglePhase = singlePhase;

                // Store the prepare info we are given.
                Debug.Assert(this.proxyPrepareInfoByteArray == null, "Unexpected value in this.proxyPrepareInfoByteArray");
                long arrayLength = prepareInfo.Length;
                this.proxyPrepareInfoByteArray = new Byte[arrayLength];
                Array.Copy(prepareInfo, this.proxyPrepareInfoByteArray, arrayLength);

                if ( this.isSinglePhase && this.canDoSinglePhase )
                {
                    ISinglePhaseNotificationInternal singlePhaseNotification = (ISinglePhaseNotificationInternal) localEnlistmentNotification;
                    state = OletxEnlistmentState.SinglePhaseCommitting;
                    // We don't call DecrementUndecidedEnlistments for Phase1 enlistments.
                    if ( DiagnosticTrace.Verbose )
                    {
                        EnlistmentNotificationCallTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            this.InternalTraceIdentifier,
                            NotificationCall.SinglePhaseCommit
                            );
                    }

                    singlePhaseNotification.SinglePhaseCommit( this );
                    enlistmentDone = true;
                }
                else
                {
                    // We need to turn the oletxRecoveryInformation into a byte array.
                    byte[] oletxRecoveryInformationByteArray = TransactionManager.ConvertToByteArray( oletxRecoveryInformation );
                        
                    state = OletxEnlistmentState.Preparing;
                        
                    // 
                    this.prepareInfoByteArray = TransactionManager.GetRecoveryInformation(
                        oletxResourceManager.oletxTransactionManager.CreationNodeName,
                        oletxRecoveryInformationByteArray
                        );

                    if ( DiagnosticTrace.Verbose )
                    {
                        EnlistmentNotificationCallTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            this.InternalTraceIdentifier,
                            NotificationCall.Prepare
                            );
                    }

                    localEnlistmentNotification.Prepare(
                        this
                        );
                    enlistmentDone = false;
                }
            }
            else if ( OletxEnlistmentState.Prepared == localState )
            {
                // We must have done our prepare work during Phase0 so just vote Yes.
                try
                {
                    localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.Prepared );
                    enlistmentDone = false;
                }
                catch ( COMException comException )
                {
                    OletxTransactionManager.ProxyException( comException );
                    throw;
                }
            }
            else if ( OletxEnlistmentState.Done == localState )
            {
                try
                {
                    // This was an early vote.  Respond ReadOnly
                    try
                    {
                        localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.ReadOnly );
                        enlistmentDone = true;
                    }
                    finally
                    {
                        FinishEnlistment();
                    }
                }
                catch ( COMException comException )
                {
                    OletxTransactionManager.ProxyException( comException );
                    throw;
                }
            }
            else
            {
                // Any other state means we should vote NO to the proxy.
                try
                {
                    localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.Failed );
                }
                catch ( COMException ex )
                {
                    // No point in rethrowing this.  We are not on an app thread and we have already told
                    // the app that the transaction is aborting.  When the app calls EnlistmentDone, we will
                    // do the final release of the ITransactionEnlistmentAsync.
                    if ( DiagnosticTrace.Verbose )
                    {
                        ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            ex );
                    }
                }
                enlistmentDone = true;
            }

            return enlistmentDone;
        }
            

        public void CommitRequest()
        {
            OletxEnlistmentState localState = OletxEnlistmentState.Active;
            IEnlistmentNotificationInternal localEnlistmentNotification = null;
            IEnlistmentShim localEnlistmentShim = null;
            bool finishEnlistment = false;

            lock ( this )
            {
                if ( OletxEnlistmentState.Prepared == state )
                {
                    localState = state = OletxEnlistmentState.Committing;
                    localEnlistmentNotification = iEnlistmentNotification;
                }
                else
                {
                    // We must have received an EnlistmentDone already.
                    localState = state;
                    localEnlistmentShim = this.EnlistmentShim;
                    finishEnlistment = true;
                }
            }

            if ( null != localEnlistmentNotification )
            {
                if ( DiagnosticTrace.Verbose )
                {
                    EnlistmentNotificationCallTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                        this.InternalTraceIdentifier,
                        NotificationCall.Commit
                        );
                }

                localEnlistmentNotification.Commit( this );
            }
            else if ( null != localEnlistmentShim )
            {
                // We need to respond to the proxy now.
                try
                {
                    localEnlistmentShim.CommitRequestDone();
                }
                catch ( COMException ex )
                {
                    // If the TM went down during our call, there is nothing special we have to do because
                    // the App doesn't expect any more notifications.  We do want to mark the enlistment
                    // to finish, however.
                    if ( ( NativeMethods.XACT_E_CONNECTION_DOWN == ex.ErrorCode ) ||
                        ( NativeMethods.XACT_E_TMNOTAVAILABLE == ex.ErrorCode )
                        )
                    {
                        finishEnlistment = true;
                        if ( DiagnosticTrace.Verbose )
                        {
                            ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                                ex );
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
                finally
                {
                    if ( finishEnlistment )
                    {
                        FinishEnlistment();
                    }
                }
            }
            return;
        }

        public void AbortRequest()
        {
            OletxEnlistmentState localState = OletxEnlistmentState.Active;
            IEnlistmentNotificationInternal localEnlistmentNotification = null;
            IEnlistmentShim localEnlistmentShim = null;
            bool finishEnlistment = false;

            lock ( this )
            {
                if ( ( OletxEnlistmentState.Active == state ) ||
                     ( OletxEnlistmentState.Prepared == state )
                   )
                {
                    localState = state = OletxEnlistmentState.Aborting;
                    localEnlistmentNotification = iEnlistmentNotification;
                }
                else
                {
                    // We must have received an EnlistmentDone already or we have
                    // a notification outstanding (Phase0 prepare).
                    localState = state;
                    if ( OletxEnlistmentState.Phase0Preparing == state )
                    {
                        this.fabricateRollback = true;
                    }
                    else
                    {
                        finishEnlistment = true;
                    }

                    localEnlistmentShim = this.EnlistmentShim;
                }
            }

            if ( null != localEnlistmentNotification )
            {
                if ( DiagnosticTrace.Verbose )
                {
                    EnlistmentNotificationCallTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                        this.InternalTraceIdentifier,
                        NotificationCall.Rollback
                        );
                }

                localEnlistmentNotification.Rollback( this );
            }
            else if ( null != localEnlistmentShim )
            {
                // We need to respond to the proxy now.
                try
                {
                    localEnlistmentShim.AbortRequestDone();
                }
                catch ( COMException ex )
                {
                    // If the TM went down during our call, there is nothing special we have to do because
                    // the App doesn't expect any more notifications.  We do want to mark the enlistment
                    // to finish, however.
                    if ( ( NativeMethods.XACT_E_CONNECTION_DOWN == ex.ErrorCode ) ||
                        ( NativeMethods.XACT_E_TMNOTAVAILABLE == ex.ErrorCode )
                        )
                    {
                        finishEnlistment = true;
                        if ( DiagnosticTrace.Verbose )
                        {
                            ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                                ex );
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
                finally
                {
                    if ( finishEnlistment )
                    {
                        FinishEnlistment();
                    }
                }
            }
            return;
        }

        public void TMDown()
        {
            // We aren't telling our enlistments about TMDown, only
            // resource managers.
            // Put this enlistment on the Reenlist list.  The Reenlist thread will get
            // started when the RMSink gets the TMDown notification.
            lock ( oletxResourceManager.reenlistList )
            {
                lock ( this )
                {
                    // Remember that we got the TMDown in case we get a Phase0Request after so we
                    // can avoid doing a Prepare to the app.
                    this.tmWentDown = true;

                    // Only move Prepared and Committing enlistments to the ReenlistList.  All others
                    // do not require a Reenlist to figure out what to do.  We save off Committing
                    // enlistments because the RM has not acknowledged the commit, so we can't
                    // call RecoveryComplete on the proxy until that has happened.  The Reenlist thread
                    // will loop until the reenlist list is empty and it will leave a Committing
                    // enlistment on the list until it is done, but will NOT call Reenlist on the proxy.
                    if ( ( OletxEnlistmentState.Prepared == state ) ||
                         ( OletxEnlistmentState.Committing == state )
                       )
                    {
                        oletxResourceManager.reenlistList.Add( this );
                    }
                }
            }

            return;
        }

        #endregion
        
        #region ITransactionPhase0NotifyAsync methods
        
        // ITransactionPhase0NotifyAsync
        public void Phase0Request(
            bool abortingHint
            )
        {
            IEnlistmentNotificationInternal localEnlistmentNotification = null;
            OletxEnlistmentState localState = OletxEnlistmentState.Active;
            OletxCommittableTransaction committableTx = null;
            bool commitNotYetCalled = false;

            if ( DiagnosticTrace.Verbose )
            {
                MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxEnlistment.Phase0Request"
                    );
            }

            committableTx = this.oletxTransaction.realOletxTransaction.committableTransaction;
            if ( null != committableTx )
            {
                // We are dealing with the committable transaction.  If Commit or BeginCommit has NOT been
                // called, then we are dealing with a situation where the TM went down and we are getting
                // a bogus Phase0Request with abortHint = false (COMPlus 

                if (!committableTx.CommitCalled )
                {
                    commitNotYetCalled = true;
                }
            }

            lock ( this )
            {
                this.aborting = abortingHint;

                // The app may have already called EnlistmentDone.  If this occurs, don't bother sending
                // the notification to the app and we don't need to tell the proxy.
                if ( OletxEnlistmentState.Active == state )
                {
                    // If we got an abort hint or we are the committable transaction and Commit has not yet been called or the TM went down,
                    // we don't want to do any more work on the transaction.  The abort notifications will be sent by the phase 1
                    // enlistment
                    if ( ( this.aborting ) || ( commitNotYetCalled ) || ( this.tmWentDown ) )
                    {
                        // There is a possible ---- where we could get the Phase0Request before we are given the
                        // shim.  In that case, we will vote "no" when we are given the shim.
                        if ( null != this.phase0Shim )
                        {
                            try
                            {
                                this.phase0Shim.Phase0Done( false );
                            }
                            // I am not going to check for XACT_E_PROTOCOL here because that check is a workaround for a 

                            catch ( COMException ex )
                            {
                                if ( DiagnosticTrace.Verbose )
                                {
                                    ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                                        ex );
                                }
                            }
                        }
                    }
                    else
                    {
                        localState = state = OletxEnlistmentState.Phase0Preparing;
                        localEnlistmentNotification = iEnlistmentNotification;
                    }
                }
            }

            // Tell the application to do the work.
            if ( null != localEnlistmentNotification )
            {
                if ( OletxEnlistmentState.Phase0Preparing == localState )
                {
                    byte[] txGuidArray = transactionGuid.ToByteArray();
                    byte[] rmGuidArray = oletxResourceManager.resourceManagerIdentifier.ToByteArray();
                    
                    byte[] temp = new byte[txGuidArray.Length + rmGuidArray.Length];
                    Thread.MemoryBarrier();
                    this.proxyPrepareInfoByteArray = temp;
                    int index = 0;
                    for ( index = 0; index < txGuidArray.Length; index++ )
                    {
                        proxyPrepareInfoByteArray[index] = 
                            txGuidArray[index];
                    }

                    for ( index = 0; index < rmGuidArray.Length; index++ )
                    {
                        proxyPrepareInfoByteArray[txGuidArray.Length + index] = 
                            rmGuidArray[index];
                    }

                    OletxRecoveryInformation oletxRecoveryInformation = new OletxRecoveryInformation(
                                                                            proxyPrepareInfoByteArray
                                                                            );
                    byte[] oletxRecoveryInformationByteArray = TransactionManager.ConvertToByteArray( oletxRecoveryInformation );

                    // 
                    this.prepareInfoByteArray = TransactionManager.GetRecoveryInformation(
                        oletxResourceManager.oletxTransactionManager.CreationNodeName,
                        oletxRecoveryInformationByteArray
                        );

                    if ( DiagnosticTrace.Verbose )
                    {
                        EnlistmentNotificationCallTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            this.InternalTraceIdentifier,
                            NotificationCall.Prepare
                            );
                    }

                    localEnlistmentNotification.Prepare( this );
                }
                else
                {
                    // We must have had a ---- between EnlistmentDone and the proxy telling
                    // us Phase0Request.  Just return.
                    if ( DiagnosticTrace.Verbose )
                    {
                        MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            "OletxEnlistment.Phase0Request"
                            );
                    }

                    return;
                }

            }
            if ( DiagnosticTrace.Verbose )
            {
                MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxEnlistment.Phase0Request"
                    );
            }

        }

        #endregion
        
        public void EnlistmentDone()
        {
            if ( DiagnosticTrace.Verbose )
            {
                MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxEnlistment.EnlistmentDone"
                    );
                EnlistmentCallbackPositiveTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentCallback.Done
                    );
            }

            IEnlistmentShim localEnlistmentShim = null;
            IPhase0EnlistmentShim localPhase0Shim = null;
            OletxEnlistmentState localState = OletxEnlistmentState.Active;
            bool finishEnlistment;
            bool localFabricateRollback = false;

            lock ( this )
            {
                localState = state;
                if ( OletxEnlistmentState.Active == state )
                {
                    // Early vote.  If we are doing Phase0, we need to unenlist.  Otherwise, just
                    // remember.
                    localPhase0Shim = this.Phase0EnlistmentShim;
                    if ( null != localPhase0Shim )
                    {
                        // We are a Phase0 enlistment and we have a vote - decrement the undecided enlistment count.
                        // We only do this for Phase0 because we don't count Phase1 durable enlistments.
                        oletxTransaction.realOletxTransaction.DecrementUndecidedEnlistments();
                    }
                    finishEnlistment = false;
                }
                else if ( OletxEnlistmentState.Preparing == state )
                {
                    // Read only vote.  Tell the proxy and go to the Done state.
                    localEnlistmentShim = this.EnlistmentShim;
                    // We don't decrement the undecided enlistment count for Preparing because we only count
                    // Phase0 enlistments and we are in Phase1 in Preparing state.
                    finishEnlistment = true;
                }
                else if ( OletxEnlistmentState.Phase0Preparing == state )
                {
                    // Read only vote to Phase0.  Tell the proxy okay and go to the Done state.
                    localPhase0Shim = this.Phase0EnlistmentShim;
                    // We are a Phase0 enlistment and we have a vote - decrement the undecided enlistment count.
                    // We only do this for Phase0 because we don't count Phase1 durable enlistments.
                    oletxTransaction.realOletxTransaction.DecrementUndecidedEnlistments();

                    // If we would have fabricated a rollback then we have already received an abort request
                    // from proxy and will not receive any more notifications.  Otherwise more notifications
                    // will be coming.
                    if ( this.fabricateRollback )
                    {
                        finishEnlistment = true;
                    }
                    else
                    {
                        finishEnlistment = false;
                    }
                }
                else if ( ( OletxEnlistmentState.Committing == state ) ||
                    ( OletxEnlistmentState.Aborting == state ) ||
                    ( OletxEnlistmentState.SinglePhaseCommitting == state )
                    )
                {
                    localEnlistmentShim = this.EnlistmentShim;
                    finishEnlistment = true;
                    // We don't decrement the undecided enlistment count for SinglePhaseCommitting because we only
                    // do it for Phase0 enlistments.
                }
                else
                {
                    throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                }

                // If this.fabricateRollback is true, it means that we are fabricating this
                // AbortRequest, rather than having the proxy tell us.  So we don't need
                // to respond to the proxy with AbortRequestDone.
                localFabricateRollback = this.fabricateRollback;

                state = OletxEnlistmentState.Done;
            }

            try
            {
                if ( null != localEnlistmentShim )
                {
                    if ( OletxEnlistmentState.Preparing == localState )
                    {
                        try
                        {
                            localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.ReadOnly );
                        }
                        finally
                        {
                            HandleTable.FreeHandle( this.phase1Handle );
                        }
                    }
                    else if ( OletxEnlistmentState.Committing == localState )
                    {
                        localEnlistmentShim.CommitRequestDone();
                    }
                    else if ( OletxEnlistmentState.Aborting == localState )
                    {
                        // If localFabricatRollback is true, it means that we are fabricating this
                        // AbortRequest, rather than having the proxy tell us.  So we don't need
                        // to respond to the proxy with AbortRequestDone.
                        if ( ! localFabricateRollback )
                        {
                            localEnlistmentShim.AbortRequestDone();
                        }
                    }
                    else if ( OletxEnlistmentState.SinglePhaseCommitting == localState )
                    {
                        localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.SinglePhase );
                    }
                    else
                    {
                        throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                    }
                }
                else if ( null != localPhase0Shim )
                {
                    if ( OletxEnlistmentState.Active == localState )
                    {
                        localPhase0Shim.Unenlist();
                    }
                    else if ( OletxEnlistmentState.Phase0Preparing == localState )
                    {
                        localPhase0Shim.Phase0Done( true );
                    }
                    else
                    {
                        throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                    }
                }

            }
            catch ( COMException ex )
            {
                // If we get an error talking to the proxy, there is nothing special we have to do because
                // the App doesn't expect any more notifications.  We do want to mark the enlistment
                // to finish, however.
                finishEnlistment = true;

                if ( DiagnosticTrace.Verbose )
                {
                    ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                        ex );
                }
            }
            finally
            {
                if ( finishEnlistment )
                {
                    FinishEnlistment();
                }
            }
            if ( DiagnosticTrace.Verbose )
            {
                MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxEnlistment.EnlistmentDone"
                    );
            }
        }


        public EnlistmentTraceIdentifier EnlistmentTraceId
        {
            get
            {
                if ( DiagnosticTrace.Verbose )
                {
                    MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                        "OletxEnlistment.get_TraceIdentifier"
                        );
                    MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                        "OletxEnlistment.get_TraceIdentifier"
                        );
                }

                return this.InternalTraceIdentifier;
            }
        }
        
        public void Prepared()
        {
            int hrResult = NativeMethods.S_OK;
            IEnlistmentShim localEnlistmentShim = null;
            IPhase0EnlistmentShim localPhase0Shim = null;
            bool localFabricateRollback = false;

            if ( DiagnosticTrace.Verbose )
            {
                MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxPreparingEnlistment.Prepared"
                    );
                EnlistmentCallbackPositiveTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentCallback.Prepared
                    );
            }

            lock ( this )
            {
                if ( OletxEnlistmentState.Preparing == state )
                {
                    localEnlistmentShim = this.EnlistmentShim;
                }
                else if ( OletxEnlistmentState.Phase0Preparing == state )
                {
                    // If the transaction is doomed or we have fabricateRollback is true because the
                    // transaction aborted while the Phase0 Prepare request was outstanding,
                    // release the WrappedTransactionPhase0EnlistmentAsync and remember that
                    // we have a pending rollback.
                    localPhase0Shim = this.Phase0EnlistmentShim;
                    if ( ( oletxTransaction.realOletxTransaction.Doomed ) ||
                         ( this.fabricateRollback )
                       )
                    {
                        // Set fabricateRollback in case we got here because the transaction is doomed.
                        this.fabricateRollback = true;
                        localFabricateRollback = this.fabricateRollback;
                    }
                }
                else
                {
                    throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                }

                state = OletxEnlistmentState.Prepared;

            }

            try
            {
                if ( null != localEnlistmentShim )
                {
                    localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.Prepared );
                }
                else if ( null != localPhase0Shim )
                {
                    // We have a vote - decrement the undecided enlistment count.  We do
                    // this after checking Doomed because ForceRollback will decrement also.
                    // We also do this only for Phase0 enlistments.
                    oletxTransaction.realOletxTransaction.DecrementUndecidedEnlistments();

                    localPhase0Shim.Phase0Done( !localFabricateRollback );
                }

                else
                    // The TM must have gone down, thus causing our interface pointer to be
                    // invalidated.  So we need to drive abort of the enlistment as if we
                    // received an AbortRequest.
                {
                    localFabricateRollback = true;
                }
                
                if ( localFabricateRollback )
                {
                    AbortRequest();
                }
            }
            catch ( COMException ex )
            {
                // If the TM went down during our call, the TMDown notification to the enlistment
                // and RM will put this enlistment on the ReenlistList, if appropriate.  The outcome
                // will be obtained by the ReenlistThread.
                if ( ( NativeMethods.XACT_E_CONNECTION_DOWN == ex.ErrorCode ) ||
                    ( NativeMethods.XACT_E_TMNOTAVAILABLE == ex.ErrorCode )
                    )
                {
                    if ( DiagnosticTrace.Verbose )
                    {
                        ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            ex );
                    }
                }
                // In the case of Phase0, there is a 






                else if ( NativeMethods.XACT_E_PROTOCOL == ex.ErrorCode )
                {
                    this.Phase0EnlistmentShim = null;
                    if ( DiagnosticTrace.Verbose )
                    {
                        ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            ex );
                    }
                }
                else
                {
                    throw;
                }
            }
            if ( DiagnosticTrace.Verbose )
            {
                MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxPreparingEnlistment.Prepared"
                    );
            }
        }


        public void ForceRollback()
        {
            ForceRollback( null );
        }

        public void ForceRollback( Exception e )
        {
            IEnlistmentShim localEnlistmentShim = null;
            IPhase0EnlistmentShim localPhase0Shim = null;

            if ( DiagnosticTrace.Verbose )
            {
                MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxPreparingEnlistment.ForceRollback"
                    );
            }

            if ( DiagnosticTrace.Warning )
            {
                EnlistmentCallbackNegativeTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentCallback.ForceRollback
                    );
            }

            lock ( this )
            {
                if ( OletxEnlistmentState.Preparing == state )
                {
                    localEnlistmentShim = this.EnlistmentShim;
                }
                else if ( OletxEnlistmentState.Phase0Preparing == state )
                {
                    localPhase0Shim = this.Phase0EnlistmentShim;
                    if ( null != localPhase0Shim )
                    {

                        // We have a vote - decrement the undecided enlistment count.  We only do this
                        // if we are Phase0 enlistment.
                        oletxTransaction.realOletxTransaction.DecrementUndecidedEnlistments();
                    }

                }
                else
                {
                    throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                }

                state = OletxEnlistmentState.Aborted;
            }

            Interlocked.CompareExchange<Exception>( ref this.oletxTransaction.realOletxTransaction.innerException, e, null );

            try
            {
                if ( null != localEnlistmentShim )
                {
                    try
                    {
                        localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.Failed );
                    }
                    finally
                    {
                        HandleTable.FreeHandle( this.phase1Handle );
                    }
                }

                if ( null != localPhase0Shim )
                {
                    localPhase0Shim.Phase0Done( false );
                }
//                else
                    // The TM must have gone down, thus causing our interface pointer to be
                    // invalidated.  The App doesn't expect any more notifications, so we can
                    // just finish the enlistment.
            }
            catch ( COMException ex )
            {
                // If the TM went down during our call, there is nothing special we have to do because
                // the App doesn't expect any more notifications.
                if ( ( NativeMethods.XACT_E_CONNECTION_DOWN == ex.ErrorCode ) ||
                    ( NativeMethods.XACT_E_TMNOTAVAILABLE == ex.ErrorCode )
                    )
                {
                    if ( DiagnosticTrace.Verbose )
                    {
                        ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            ex );
                    }
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                FinishEnlistment();
            }
            if ( DiagnosticTrace.Verbose )
            {
                MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxPreparingEnlistment.ForceRollback"
                    );
            }
        }
        
        public void Committed()
        {
            IEnlistmentShim localEnlistmentShim = null;
            if ( DiagnosticTrace.Verbose )
            {
                MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxSinglePhaseEnlistment.Committed"
                    );
                EnlistmentCallbackPositiveTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentCallback.Committed
                    );
            }

            lock ( this )
            {
                if (!isSinglePhase || (OletxEnlistmentState.SinglePhaseCommitting != state))
                {
                    throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                }
                state = OletxEnlistmentState.Committed;
                localEnlistmentShim = this.EnlistmentShim;
            }

            try
            {
                // This may be the result of a reenlist, which means we don't have a
                // reference to the proxy.
                if ( null != localEnlistmentShim )
                {
                    localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.SinglePhase );
                }
            }
            catch ( COMException ex )
            {
                // If the TM went down during our call, there is nothing special we have to do because
                // the App doesn't expect any more notifications.
                if ( ( NativeMethods.XACT_E_CONNECTION_DOWN == ex.ErrorCode ) ||
                    ( NativeMethods.XACT_E_TMNOTAVAILABLE == ex.ErrorCode )
                    )
                {
                    if ( DiagnosticTrace.Verbose )
                    {
                        ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            ex );
                    }
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                FinishEnlistment();
            }
            if ( DiagnosticTrace.Verbose )
            {
                MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxSinglePhaseEnlistment.Committed"
                    );
            }
        }


        public void Aborted()
        {
            Aborted( null );
        }

    
        public void Aborted(Exception e)
        {
            IEnlistmentShim localEnlistmentShim = null;
            if ( DiagnosticTrace.Verbose )
            {
                MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxSinglePhaseEnlistment.Aborted"
                    );
            }

            if ( DiagnosticTrace.Warning )
            {
                EnlistmentCallbackNegativeTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentCallback.Aborted
                    );
            }

            lock ( this )
            {
                if (!isSinglePhase || (OletxEnlistmentState.SinglePhaseCommitting != state))
                {
                    throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                }
                state = OletxEnlistmentState.Aborted;
                
                localEnlistmentShim = this.EnlistmentShim;
            }

            Interlocked.CompareExchange<Exception>( ref this.oletxTransaction.realOletxTransaction.innerException, e, null );

            try
            {
                if ( null != localEnlistmentShim )
                {
                    localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.Failed );
                }
            }
            catch ( COMException ex )
            {
                // If the TM went down during our call, there is nothing special we have to do because
                // the App doesn't expect any more notifications.
                if ( ( NativeMethods.XACT_E_CONNECTION_DOWN == ex.ErrorCode ) ||
                    ( NativeMethods.XACT_E_TMNOTAVAILABLE == ex.ErrorCode )
                    )
                {
                    if ( DiagnosticTrace.Verbose )
                    {
                        ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            ex );
                    }
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                FinishEnlistment();
            }
            if ( DiagnosticTrace.Verbose )
            {
                MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxSinglePhaseEnlistment.Aborted"
                    );
            }
        }


        public void InDoubt()
        {
            InDoubt( null );
        }


        public void InDoubt(Exception e)
        {
            IEnlistmentShim localEnlistmentShim = null;
            if ( DiagnosticTrace.Verbose )
            {
                MethodEnteredTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxSinglePhaseEnlistment.InDoubt"
                    );
            }

            if ( DiagnosticTrace.Warning )
            {
                EnlistmentCallbackNegativeTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    this.InternalTraceIdentifier,
                    EnlistmentCallback.InDoubt
                    );
            }

            lock ( this )
            {
                if (!isSinglePhase || (OletxEnlistmentState.SinglePhaseCommitting != state))
                {
                    throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
                }
                state = OletxEnlistmentState.InDoubt;
                localEnlistmentShim = this.EnlistmentShim;
            }

            lock ( this.oletxTransaction.realOletxTransaction )
            {
                if ( this.oletxTransaction.realOletxTransaction.innerException == null )
                {
                    this.oletxTransaction.realOletxTransaction.innerException = e;
                }
            }

            try
            {
                if ( null != localEnlistmentShim )
                {
                    localEnlistmentShim.PrepareRequestDone( OletxPrepareVoteType.InDoubt );
                }
            }
            catch ( COMException ex )
            {
                // If the TM went down during our call, there is nothing special we have to do because
                // the App doesn't expect any more notifications.
                if ( ( NativeMethods.XACT_E_CONNECTION_DOWN == ex.ErrorCode ) ||
                    ( NativeMethods.XACT_E_TMNOTAVAILABLE == ex.ErrorCode )
                    )
                {
                    if ( DiagnosticTrace.Verbose )
                    {
                        ExceptionConsumedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                            ex );
                    }
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                FinishEnlistment();
            }
            if ( DiagnosticTrace.Verbose )
            {
                MethodExitedTraceRecord.Trace( SR.GetString( SR.TraceSourceOletx ),
                    "OletxSinglePhaseEnlistment.InDoubt"
                    );
            }
        }

        public byte[] GetRecoveryInformation()
        {
            if ( this.prepareInfoByteArray == null )
            {
                Debug.Assert( false, string.Format( null, "this.prepareInfoByteArray == null in RecoveryInformation()" ));
                throw TransactionException.CreateEnlistmentStateException(SR.GetString(SR.TraceSourceOletx), null, this.DistributedTxId);
            }
            return this.prepareInfoByteArray;
        }


        public InternalEnlistment InternalEnlistment
        {
            get
            {
                return this.internalEnlistment;
            }
            
            set
            {
                this.internalEnlistment = value;
            }
        }
    }
}
