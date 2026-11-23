#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <atomic>
#include <filesystem>
#include <new>
#include <string>
#include <vector>

// Explorer 中只加载这层小型原生扩展，文件移动和 UI 均由独立的 DeskNest 进程处理。
static HINSTANCE moduleHandle = nullptr;
static std::atomic<long> liveObjects{0};
static const CLSID commandId =
    {0x683abcce, 0x019f, 0x436b, {0xa2, 0x87, 0xca, 0xe2, 0xa3, 0xbf, 0xe6, 0x65}};

static std::wstring ExecutablePath()
{
    std::vector<wchar_t> buffer(32768);
    const auto length = GetModuleFileNameW(moduleHandle, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    return (std::filesystem::path(buffer.data()).parent_path() / L"DeskNest.exe").wstring();
}

static std::string JsonString(const std::wstring& value)
{
    const auto length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) throw std::runtime_error("Invalid file name");
    std::string utf8(length, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), utf8.data(), length, nullptr, nullptr);
    std::string result = "\"";
    constexpr char hex[] = "0123456789abcdef";
    for (const unsigned char character : utf8)
    {
        if (character == '"' || character == '\\')
        {
            result += '\\';
            result += static_cast<char>(character);
        }
        else if (character < 0x20)
        {
            result += "\\u00";
            result += hex[character >> 4];
            result += hex[character & 15];
        }
        else result += static_cast<char>(character);
    }
    return result + "\"";
}

static HRESULT SubmitSelection(IShellItemArray* items)
{
    if (!items) return E_INVALIDARG;
    DWORD count = 0;
    HRESULT status = items->GetCount(&count);
    if (FAILED(status)) return status;
    if (count == 0 || count > 10000) return E_INVALIDARG;

    // 一份清单保存整个选择集，避免 %1 静态菜单对每个文件各启动一次程序。
    std::string json = "{\"Action\":\"new\",\"Paths\":[";
    for (DWORD index = 0; index < count; ++index)
    {
        IShellItem* item = nullptr;
        status = items->GetItemAt(index, &item);
        if (FAILED(status)) return status;
        PWSTR path = nullptr;
        status = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
        item->Release();
        if (FAILED(status)) return status;
        const std::wstring filePath(path);
        CoTaskMemFree(path);
        if (index > 0) json += ',';
        json += JsonString(filePath);
        if (json.size() > 4000000) return E_INVALIDARG;
    }
    json += "]}";

    PWSTR localData = nullptr;
    status = SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localData);
    if (FAILED(status)) return status;
    const auto directory = std::filesystem::path(localData) / L"DeskNest" / L"Requests";
    CoTaskMemFree(localData);
    std::filesystem::create_directories(directory);
    GUID requestId{};
    status = CoCreateGuid(&requestId);
    if (FAILED(status)) return status;
    wchar_t identifier[40]{};
    StringFromGUID2(requestId, identifier, 40);
    const auto request = directory / (std::wstring(identifier) + L".json");
    const auto file = CreateFileW(request.c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    DWORD written = 0;
    const auto saved = WriteFile(file, json.data(), static_cast<DWORD>(json.size()), &written, nullptr);
    const auto writeError = GetLastError();
    CloseHandle(file);
    if (!saved || written != json.size())
    {
        DeleteFileW(request.c_str());
        return HRESULT_FROM_WIN32(saved ? ERROR_WRITE_FAULT : writeError);
    }

    const auto executable = ExecutablePath();
    const auto arguments = L"--shell-request \"" + request.wstring() + L"\"";
    const auto launched = ShellExecuteW(nullptr, L"open", executable.c_str(), arguments.c_str(), nullptr, SW_SHOWNORMAL);
    if (reinterpret_cast<INT_PTR>(launched) <= 32)
    {
        DeleteFileW(request.c_str());
        return E_FAIL;
    }
    return S_OK;
}

class ExplorerCommand final : public IExplorerCommand
{
    std::atomic<ULONG> references{1};
public:
    ExplorerCommand() { ++liveObjects; }
    ~ExplorerCommand() { --liveObjects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id == IID_IUnknown || id == __uuidof(IExplorerCommand))
            *result = static_cast<IExplorerCommand*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const auto remaining = --references;
        if (remaining == 0) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* title) override
    { return SHStrDupW(L"新建收纳卡片并收纳所选文件", title); }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        try { return SHStrDupW(ExecutablePath().c_str(), icon); }
        catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* text) override
    { return SHStrDupW(L"将全部所选文件移动到同一张新的桌面收纳卡片", text); }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* id) override
    {
        if (!id) return E_POINTER;
        *id = commandId;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        if (!state) return E_POINTER;
        *state = ECS_HIDDEN;
        DWORD count = 0;
        SFGAOF attributes = 0;
        if (items && SUCCEEDED(items->GetCount(&count)) && count > 0 && count <= 10000 &&
            SUCCEEDED(items->GetAttributes(SIATTRIBFLAGS_AND, SFGAO_FILESYSTEM, &attributes)) &&
            (attributes & SFGAO_FILESYSTEM)) *state = ECS_ENABLED;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override
    {
        // COM 边界不允许 C++ 异常进入 Explorer；失败时源文件尚未被触碰。
        try { return SubmitSelection(items); }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override
    {
        if (!flags) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (commands) *commands = nullptr;
        return E_NOTIMPL;
    }
};

class CommandFactory final : public IClassFactory
{
    std::atomic<ULONG> references{1};
public:
    CommandFactory() { ++liveObjects; }
    ~CommandFactory() { --liveObjects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id != IID_IUnknown && id != IID_IClassFactory) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const auto remaining = --references;
        if (remaining == 0) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID id, void** result) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto command = new (std::nothrow) ExplorerCommand();
        if (!command) return E_OUTOFMEMORY;
        const auto status = command->QueryInterface(id, result);
        command->Release();
        return status;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    { if (lock) ++liveObjects; else --liveObjects; return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID id, REFIID interfaceId, void** result)
{
    if (id != commandId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) CommandFactory();
    if (!factory) return E_OUTOFMEMORY;
    const auto status = factory->QueryInterface(interfaceId, result);
    factory->Release();
    return status;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return liveObjects == 0 ? S_OK : S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        moduleHandle = module;
        // 静态 CRT 需要线程通知，不能调用 DisableThreadLibraryCalls。
    }
    return TRUE;
}
