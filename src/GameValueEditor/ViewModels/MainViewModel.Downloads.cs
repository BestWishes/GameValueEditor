using GameValueEditor.Services;

namespace GameValueEditor.ViewModels;

public sealed partial class MainViewModel
{
    private DownloadOperation? _downloadOperation;
    public bool IsDownloadActive => _downloadOperation is not null;
    public bool CanCancelDownload => _downloadOperation?.CanCancel == true;
    public System.Windows.Visibility DownloadCancelVisibility => IsDownloadActive
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;

    public void CancelDownload()
    {
        if (_downloadOperation?.Cancel() == true)
        {
            StatusText = "正在取消下载…";
            OnPropertyChanged(nameof(CanCancelDownload));
        }
    }

    private DownloadOperation BeginDownload(Action<DownloadProgressSnapshot> callback)
    {
        if (IsDownloadActive) throw new InvalidOperationException("已有下载正在处理，请等待完成或取消下载。");
        var operation = new DownloadOperation();
        operation.SetCallback(snapshot =>
        {
            if (!ReferenceEquals(_downloadOperation, operation) || operation.IsCanceled || _isShuttingDown) return;
            OnPropertyChanged(nameof(CanCancelDownload));
            callback(snapshot);
        });
        _downloadOperation = operation;
        NotifyDownloadControls();
        return operation;
    }

    private void EndDownload(DownloadOperation operation)
    {
        if (ReferenceEquals(_downloadOperation, operation)) _downloadOperation = null;
        operation.Dispose();
        NotifyDownloadControls();
    }

    private void NotifyDownloadControls()
    {
        OnPropertyChanged(nameof(IsDownloadActive));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(DownloadCancelVisibility));
        NotifyModuleControls();
        NotifyApplicationUpdateState();
    }

    private sealed class DownloadOperation : IProgress<DownloadProgressSnapshot>, IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private IProgress<DownloadProgressSnapshot> _uiProgress = new Progress<DownloadProgressSnapshot>();
        private bool _canCancel = true;
        public void SetCallback(Action<DownloadProgressSnapshot> callback) => _uiProgress = new Progress<DownloadProgressSnapshot>(callback);
        public CancellationToken Token => _cancellation.Token;
        public bool IsCanceled => _cancellation.IsCancellationRequested;
        public bool CanCancel { get { lock (_gate) return _canCancel && !IsCanceled; } }
        public bool Cancel()
        {
            lock (_gate)
            {
                if (!_canCancel || IsCanceled) return false;
                _cancellation.Cancel();
                return true;
            }
        }
        public void Report(DownloadProgressSnapshot value)
        {
            lock (_gate)
            {
                if (value.Phase is DownloadPhase.Verifying or DownloadPhase.Installing)
                {
                    Token.ThrowIfCancellationRequested();
                    _canCancel = false;
                }
            }
            _uiProgress.Report(value);
        }
        public void Dispose() { lock (_gate) { _canCancel = false; _cancellation.Dispose(); } }
    }
}
