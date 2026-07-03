using System;
using System.Collections.Generic;
using System.Windows.Input;
using IJPSystem.Platform.Domain.Common;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// "Drawing Panel.vi" (Edit Panel) 로직 (MVVM).
    /// 펜 브러시(굵기) + 클릭/드래그 그리기 + ROI 영역 채우기 + Undo/Redo.
    /// 그리기 상호작용/렌더링은 DrawingPanelView 코드비하인드가 담당하고, 여기서는 상태·로직만 관리.
    /// </summary>
    public sealed class DrawingPanelViewModel : ViewModelBase
    {
        private readonly Stack<bool[,]> _undo = new Stack<bool[,]>();
        private readonly Stack<bool[,]> _redo = new Stack<bool[,]>();

        public PixelGrid Grid { get; private set; }

        /// <summary>그리드 내용이 바뀌면 발생 → View 가 다시 렌더링.</summary>
        public event Action? Changed;

        public Action<bool[,]>? OnApplyDraw { get; set; }
        public Action<bool[,]>? OnSave { get; set; }

        public DrawingPanelViewModel(int size = 5)
        {
            _size = size;
            Grid = new PixelGrid(size, size);

            ApplyDrawCommand   = new RelayCommand(_ => { OnApplyDraw?.Invoke(Grid.Snapshot()); StatusText = "Apply Draw 반영"; });
            EraserCommand      = new RelayCommand(_ => Mode = Mode == DrawMode.Erase ? DrawMode.Draw : DrawMode.Erase);
            ClearCanvasCommand = new RelayCommand(_ => { PushUndo(); Grid.Clear(); Raise(); StatusText = "캔버스 비움"; });
            FillCommand        = new RelayCommand(_ => { Mode = DrawMode.RoiFill; StatusText = "Fill — 채울 영역을 드래그하세요."; });
            AutoFillCommand    = new RelayCommand(_ => { PushUndo(); Grid.AutoFillEnclosed(); Raise(); StatusText = "Auto Fill 적용"; });
            UndoCommand        = new RelayCommand(_ => Undo(), _ => _undo.Count > 0);
            RedoCommand        = new RelayCommand(_ => Redo(), _ => _redo.Count > 0);
            SaveCommand        = new RelayCommand(_ => OnSave?.Invoke(Grid.Snapshot()));
            ApplySizeCommand   = new RelayCommand(_ => ApplySize());
            SizeUpCommand      = new RelayCommand(_ => { Size += 1; ApplySize(); });
            SizeDownCommand    = new RelayCommand(_ => { Size -= 1; ApplySize(); }, _ => Size > 1);
        }

        // ---- 상태 ----
        private DrawMode _mode = DrawMode.Draw;
        public DrawMode Mode { get => _mode; set { if (SetProperty(ref _mode, value)) OnPropertyChanged(nameof(ModeText)); } }
        public string ModeText => Mode switch
        {
            DrawMode.Erase => "지우기",
            DrawMode.RoiFill => "ROI 채우기(드래그)",
            _ => "그리기"
        };

        private int _penWidth = 1;
        /// <summary>펜 굵기(점 단위). Pen Width.</summary>
        public int PenWidth { get => _penWidth; set => SetProperty(ref _penWidth, Math.Max(1, value)); }

        private int _size;
        public int Size { get => _size; set { if (SetProperty(ref _size, Math.Max(1, value))) OnPropertyChanged(nameof(SizeText)); } }
        public string SizeText => $"{Size}x{Size}";

        private string _status = "펜으로 그리거나 드래그하세요.";
        public string StatusText { get => _status; set => SetProperty(ref _status, value); }

        // ---- 커맨드 ----
        public ICommand ApplyDrawCommand { get; }
        public ICommand EraserCommand { get; }
        public ICommand ClearCanvasCommand { get; }
        public ICommand FillCommand { get; }
        public ICommand AutoFillCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ApplySizeCommand { get; }
        public ICommand SizeUpCommand { get; }
        public ICommand SizeDownCommand { get; }

        // ---- 그리기 스트로크 (View 코드비하인드에서 호출) ----

        /// <summary>스트로크 시작(마우스 다운) → 여기서 Undo 1회 기록.</summary>
        public void BeginStroke() => PushUndo();

        /// <summary>펜 브러시로 (r,c) 칠하기(드래그 중 반복 호출). 모드에 따라 on/off.</summary>
        public void PaintAt(int r, int c)
        {
            if (Mode == DrawMode.RoiFill) return; // ROI 는 CommitRoi 로 처리
            Grid.PaintBrush(r, c, PenWidth, Mode != DrawMode.Erase);
            Raise();
        }

        /// <summary>ROI 사각 영역 채우기(마우스 업). 점 ROI면 단일 셀.</summary>
        public void CommitRoi(int r0, int c0, int r1, int c1)
        {
            Grid.FillRoi(r0, c0, r1, c1, true);
            Raise();
            StatusText = "ROI 영역 채움";
        }

        private void ApplySize()
        {
            PushUndo();
            Grid.Resize(Size, Size);
            Raise();
            StatusText = $"그리드 {SizeText}";
        }

        // ---- Undo/Redo ----
        private void PushUndo() { _undo.Push(Grid.Snapshot()); _redo.Clear(); }
        private void Undo() { if (_undo.Count == 0) return; _redo.Push(Grid.Snapshot()); Grid.Restore(_undo.Pop()); Raise(); StatusText = "실행 취소"; }
        private void Redo() { if (_redo.Count == 0) return; _undo.Push(Grid.Snapshot()); Grid.Restore(_redo.Pop()); Raise(); StatusText = "다시 실행"; }

        private void Raise() { Changed?.Invoke(); OnPropertyChanged(nameof(Grid)); }
    }
}
