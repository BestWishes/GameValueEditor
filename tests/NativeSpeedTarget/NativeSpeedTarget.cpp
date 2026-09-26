extern "C"
{
    __declspec(dllimport) int __stdcall QueryPerformanceCounter(long long* value);
    __declspec(dllimport) int __stdcall QueryPerformanceFrequency(long long* value);
    __declspec(dllimport) void __stdcall Sleep(unsigned long milliseconds);
    __declspec(dllimport) unsigned long __stdcall GetCurrentProcessId();
    __declspec(dllimport) void* __stdcall GetStdHandle(unsigned long handle);
    __declspec(dllimport) int __stdcall WriteFile(
        void* file, const void* buffer, unsigned long count, unsigned long* written, void* overlapped);
    __declspec(dllimport) void __stdcall ExitProcess(unsigned int code);
}

static void WriteText(const char* text, unsigned long length)
{
    unsigned long written = 0;
    WriteFile(GetStdHandle(static_cast<unsigned long>(-11)), text, length, &written, nullptr);
}

static void WriteUnsigned(unsigned long long value)
{
    char buffer[32]{};
    unsigned long cursor = 31;
    buffer[cursor--] = '\n';
    do
    {
        buffer[cursor--] = static_cast<char>('0' + value % 10);
        value /= 10;
    } while (value != 0);
    WriteText(buffer + cursor + 1, 31 - cursor);
}

extern "C" void mainCRTStartup()
{
    long long frequency = 0;
    long long start = 0;
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&start);
    WriteText("READY ", 6);
    WriteUnsigned(GetCurrentProcessId());

    for (int sample = 0; sample < 4; ++sample)
    {
        Sleep(1000);
        long long current = 0;
        QueryPerformanceCounter(&current);
        WriteUnsigned(static_cast<unsigned long long>((current - start) * 1000 / frequency));
    }
    ExitProcess(0);
}
