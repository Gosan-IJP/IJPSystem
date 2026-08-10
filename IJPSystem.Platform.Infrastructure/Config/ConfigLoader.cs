using IJPSystem.Platform.Domain.Models.Config;
using IJPSystem.Platform.Domain.Models.IO;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace IJPSystem.Platform.Infrastructure.Config
{
    public class ConfigLoader
    {
        private readonly JsonSerializerOptions _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true // 대소문자 구분 없이 매핑 (매우 중요)
        };
        public AppSettings LoadAppSettings(string path)
        {
            try
            {
                if (!File.Exists(path)) return new AppSettings();
                string json = File.ReadAllText(path);
                // _options(대소문자 무시) 사용 — 다른 로더와 동일. 미사용 시 키 표기 차이로
                // DriverMode 등이 바인딩 안 돼 조용히 기본값(Virtual)으로 떨어짐.
                return JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings(); // 에러 시 기본값 반환
            }
        }

        public void SaveAppSettings(string path, AppSettings settings)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, options));
        }
        // --- IO 설정 로드는 기존과 동일하게 유지 ---
        public IOConfig LoadIOConfig(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);
            string jsonContent = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<IOConfig>(jsonContent, _options);
            return config ?? new IOConfig();
        }

        /// <summary>
        /// [수정됨] 계층형 MotorConfig.json 파일을 로드합니다.
        /// </summary>
        /// <returns>MotionAxisRoot 객체 (내부에 MotionAxisList 포함)</returns>
        public VisionCameraRoot LoadVisionConfig(string filePath)
        {
            if (!File.Exists(filePath)) return new VisionCameraRoot();
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<VisionCameraRoot>(json, _options) ?? new VisionCameraRoot();
        }

        /// <summary>드랍와쳐 OpenCV 액적분석 파라미터 로드. 파일 없으면 기본값(가상 검증용).</summary>
        public DropWatcherProcessorConfig LoadDropWatcherConfig(string filePath)
        {
            if (!File.Exists(filePath)) return new DropWatcherProcessorConfig();
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<DropWatcherProcessorConfig>(json, _options) ?? new DropWatcherProcessorConfig();
        }

        /// <summary>드랍와쳐 파라미터 저장(캘리브레이션: µm/px, 노즐면 Y 등). 폴더 없으면 생성.</summary>
        public void SaveDropWatcherConfig(string filePath, DropWatcherProcessorConfig cfg)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 기본 인코더는 비ASCII 를 \uXXXX 로 escape 한다 — 저장 한 번에 설정 파일의 한글 주석이
            // 읽을 수 없는 문자열로 변해 손으로 열어볼 수 없게 된다.
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(cfg, options));
        }

        /// <summary>
        /// 메니스커스 압력 컨트롤러(DMD, Modbus RTU) 설정 로드. 파일 없으면 null.
        ///
        /// <para>
        /// null 을 돌려주는 이유: 파일이 없을 때 호출부가 <b>AppConfig 의 옛 키로 폴백</b>해야 한다.
        /// 여기서 기본값을 만들어 주면 그 폴백이 조용히 죽어, 현장에서 맞춰 둔 COM 포트가
        /// 배포 직후 기본값(COM3)으로 바뀐다. 다른 로더들과 다른 점이므로 주의.
        /// </para>
        /// </summary>
        public Devices.Meniscus.DmdConfig? LoadMeniscusConfig(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Devices.Meniscus.DmdConfig>(json, _options);
        }

        /// <summary>iCore 스트로브(Modbus RTU) 설정 로드. 파일 없으면 기본값(가상 모드에선 미사용).</summary>
        public StrobeConfig LoadStrobeConfig(string filePath)
        {
            if (!File.Exists(filePath)) return new StrobeConfig();
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<StrobeConfig>(json, _options) ?? new StrobeConfig();
        }

        /// <summary>스트로브 설정 저장. 폴더 없으면 생성.</summary>
        public void SaveStrobeConfig(string filePath, StrobeConfig cfg)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 기본 인코더는 비ASCII 를 \uXXXX 로 escape 한다 — 저장 한 번에 설정 파일의 한글 주석이
            // 읽을 수 없는 문자열로 변해 손으로 열어볼 수 없게 된다.
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(cfg, options));
        }

        /// <summary>드랍와쳐 하드웨어 트리거 체인 설정 로드. 파일 없으면 기본값.</summary>
        public TriggerChainSettings LoadTriggerChainConfig(string filePath)
        {
            if (!File.Exists(filePath)) return new TriggerChainSettings();
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<TriggerChainSettings>(json, _options) ?? new TriggerChainSettings();
        }

        /// <summary>트리거 체인 설정 저장. 폴더 없으면 생성.</summary>
        public void SaveTriggerChainConfig(string filePath, TriggerChainSettings cfg)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 기본 인코더는 비ASCII 를 \uXXXX 로 escape 한다 — 저장 한 번에 설정 파일의 한글 주석이
            // 읽을 수 없는 문자열로 변해 손으로 열어볼 수 없게 된다.
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(cfg, options));
        }

        public MotionAxisRoot LoadMotionConfig(string filePath)
        {
            // 1. 파일 존재 확인
            if (!File.Exists(filePath)) throw new FileNotFoundException($"파일을 찾을 수 없습니다: {filePath}");

            // 2. 파일 읽기
            string jsonContent = File.ReadAllText(filePath);

            // 3. 역직렬화 (중요: 반환 타입을 MotionAxisRoot로 변경)
            // 우리가 만든 JSON의 최상위 부모는 MotionAxisRoot 클래스입니다.
            var root = JsonSerializer.Deserialize<MotionAxisRoot>(jsonContent, _options);

            // 4. 결과 반환
            return root ?? new MotionAxisRoot();
        }
    }
}