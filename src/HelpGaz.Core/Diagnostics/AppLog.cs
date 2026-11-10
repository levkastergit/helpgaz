using System.Text;

namespace HelpGaz.Core.Diagnostics;

/// <summary>
/// Лог программы: запросы к сайту, выбор договора для каждой папки, результат по каждому файлу, ошибки.
/// Один файл на день в папке logs, файлы старше 30 дней удаляются. Пароль и токен сюда не пишутся.
/// </summary>
public static class AppLog
{
    private const int KeepDays = 30;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _directory;
    private static DateOnly _day;

    /// <summary>Файл, в который сейчас идёт запись; null до первой записи.</summary>
    public static string? CurrentFile { get; private set; }

    public static string? Directory => _directory;

    public static void Init(string directory)
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
            _directory = directory;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                foreach (var old in new DirectoryInfo(directory).GetFiles("helpgaz-*.log"))
                    if (old.LastWriteTime < DateTime.Now.AddDays(-KeepDays))
                        old.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : message + Environment.NewLine + error);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_directory is null) return;
            try
            {
                var now = DateTime.Now;
                var day = DateOnly.FromDateTime(now);
                if (_writer is null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    CurrentFile = Path.Combine(_directory, $"helpgaz-{now:yyyy-MM-dd}.log");
                    // Старый процесс после автообновления может ещё писать в тот же файл, поэтому доступ общий.
                    var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
                }
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
