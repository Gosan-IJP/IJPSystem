using System.Collections.Generic;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>헤드 토출(스핏). Meteor PCC / DPN16 으로 노즐을 주파수 F로 토출.</summary>
    public interface ISpit
    {
        void Start(IReadOnlyList<int> nozzles, double frequencyHz);
        void Stop();
        double FrequencyHz { get; }
    }

    /// <summary>토출 스텁(실장비 미연결 시 값만 보관).</summary>
    public sealed class SpitStub : ISpit
    {
        public double FrequencyHz { get; private set; }
        public void Start(IReadOnlyList<int> nozzles, double f) => FrequencyHz = f; // TODO: Meteor spit
        public void Stop() { }
    }
}
