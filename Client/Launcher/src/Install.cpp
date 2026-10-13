#include "Install.h"
#include "Http.h"
#include "Update.h"
#include "Util.h"
#include "miniz.h"
#include <windows.h>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <thread>

namespace fs = std::filesystem;

uint64_t FreeBytes(const std::string& dir)
{
    ULARGE_INTEGER avail{};
    fs::path p = U8Path(dir);
    std::error_code ec;
    while (!p.empty() && !fs::exists(p, ec) && p.has_parent_path() && p.parent_path() != p) p = p.parent_path();
    return GetDiskFreeSpaceExW(p.c_str(), &avail, nullptr, nullptr) ? avail.QuadPart : 0;
}

static size_t WriteOut(void* opaque, mz_uint64, const void* buf, size_t n)
{
    auto* out = static_cast<std::ofstream*>(opaque);
    out->write(static_cast<const char*>(buf), (std::streamsize)n);
    return *out ? n : 0;
}

// A zip entry name safe to put under `root`: relative, no "..", no drive letters.
static bool SafeName(const std::string& n)
{
    if (n.empty() || n[0] == '/' || n[0] == '\\' || n.find(':') != std::string::npos) return false;
    size_t s = 0;
    while (s <= n.size())
    {
        size_t e = n.find_first_of("/\\", s);
        if (e == std::string::npos) e = n.size();
        if (n.compare(s, e - s, "..") == 0) return false;
        s = e + 1;
    }
    return true;
}

static void Run(InstallOptions o, std::shared_ptr<InstallJob> job)
{
    auto fail = [&](const std::string& why) { std::lock_guard<std::mutex> lk(job->mtx); job->error = why; };
    std::error_code ec;
    fs::create_directories(U8Path(o.parentDir), ec);
    std::string zip = PathU8(U8Path(o.parentDir) / L"Campus.download.zip");

    // ---- 1. download (continues a partial download; a damaged one is fetched once more from scratch)
    for (int attempt = 0; attempt < 2; attempt++)
    {
        job->phase = 0;
        HttpResult r = HttpDownloadResume(o.url, o.token, zip, &job->got, &job->total, &job->cancel);
        if (job->cancel) { job->done = true; return; }
        if (!r.Ok()) { fail(r.Message()); job->done = true; return; }
        job->phase = 1;
        if (Sha256File(zip) == o.sha) break;
        fs::remove(U8Path(zip), ec);
        if (attempt == 1) { fail("The download was damaged twice - check your connection and try again."); job->done = true; return; }
    }

    // ---- 2. unpack
    job->phase = 2;
    job->got = 0; job->total = 0;
    FILE* f = _wfopen(U8Path(zip).c_str(), L"rb");
    if (!f) { fail("Couldn't open the download (an antivirus may be scanning it) - try again."); job->done = true; return; }
    _fseeki64(f, 0, SEEK_END);
    mz_uint64 zsize = (mz_uint64)_ftelli64(f);
    _fseeki64(f, 0, SEEK_SET);
    mz_zip_archive za{};
    if (!mz_zip_reader_init_cfile(&za, f, zsize, 0))
    {
        fclose(f);
        fail("The download isn't a readable zip.");
        job->done = true;
        return;
    }
    mz_uint count = mz_zip_reader_get_num_files(&za);
    uint64_t all = 0;
    std::string exeDir;
    for (mz_uint i = 0; i < count; i++)
    {
        mz_zip_archive_file_stat st;
        if (!mz_zip_reader_file_stat(&za, i, &st)) continue;
        all += st.m_uncomp_size;
        std::string n = st.m_filename;
        size_t slash = n.find_last_of("/\\");
        std::string base = slash == std::string::npos ? n : n.substr(slash + 1);
        if (_stricmp(base.c_str(), "Recroom_Release.exe") == 0 && (exeDir.empty() || n.size() < exeDir.size()))
            exeDir = slash == std::string::npos ? "" : n.substr(0, slash);
    }
    job->total = all;
    fs::path root = U8Path(o.parentDir);
    std::string err;
    for (mz_uint i = 0; i < count && err.empty(); i++)
    {
        if (job->cancel) break;
        mz_zip_archive_file_stat st;
        if (!mz_zip_reader_file_stat(&za, i, &st)) { err = "The download is damaged."; break; }
        std::string n = st.m_filename;
        if (!SafeName(n)) continue;                                  // never write outside the install folder
        fs::path dst = root / U8Path(n);
        if (mz_zip_reader_is_file_a_directory(&za, i)) { fs::create_directories(dst, ec); continue; }
        fs::create_directories(dst.parent_path(), ec);
        std::ofstream out(dst, std::ios::binary | std::ios::trunc);
        if (!out) { err = "Couldn't write " + PathU8(dst) + " - is the game still running?"; break; }
        if (!mz_zip_reader_extract_to_callback(&za, i, WriteOut, &out, 0))
            err = "Couldn't unpack " + n + " - is the disk full?";
        job->got += st.m_uncomp_size;
    }
    mz_zip_reader_end(&za);
    fclose(f);
    if (job->cancel) { job->done = true; return; }                  // the zip stays: Install again unpacks it
    if (!err.empty()) { fail(err); job->done = true; return; }

    fs::path gameDir = exeDir.empty() ? root : root / U8Path(exeDir);
    if (!fs::is_regular_file(gameDir / L"Recroom_Release.exe", ec)) { fail("The download has no Recroom_Release.exe in it."); job->done = true; return; }
    fs::remove(U8Path(zip), ec);
    {
        std::lock_guard<std::mutex> lk(job->mtx);
        job->gameDir = PathU8(gameDir);
    }
    job->ok = true;
    job->done = true;
}

void StartInstall(const InstallOptions& o, std::shared_ptr<InstallJob> job)
{
    std::thread([o, job] { Run(o, job); }).detach();
}
