using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.Diagnostics;

// 要清空的数据表名
string[] tableNames = { "WORK_QUEUE", "WC_LOCK" };
string? backupPath = null; // 存储备份文件路径

// 处理右键菜单安装/卸载
if (args.Length > 0)
{
    if (args[0].Equals("/install", StringComparison.OrdinalIgnoreCase))
    {
        InstallContextMenu();
        return;
    }

    if (args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
    {
        UninstallContextMenu();
        return;
    }
}

// 确定操作目录
var targetDir = args.Length > 0 && Directory.Exists(args[0])
                    ? args[0]
                    : Environment.CurrentDirectory;

// 查找SVN根目录
var svnRootDir = FindSvnRootDirectory(targetDir);
if (string.IsNullOrEmpty(svnRootDir))
{
    Console.WriteLine("未找到SVN根目录");
    Console.WriteLine("请在SVN管理的目录结构中使用此工具");
    Console.WriteLine("\n按任意键退出...");
    Console.ReadKey();
    return;
}

var wcDbPath = Path.Combine(svnRootDir, ".svn", "wc.db");
Console.WriteLine($"找到SVN数据库: {wcDbPath}");

// 处理文件只读属性
var fileInfo = new FileInfo(wcDbPath);
if (fileInfo.IsReadOnly)
{
    try
    {
        fileInfo.IsReadOnly = false;
        Console.WriteLine("已将数据库文件从只读改为可写");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"无法修改文件属性: {ex.Message}");
        Console.WriteLine("操作无法继续，程序退出");
        Console.ReadKey();
        return;
    }
}

// 创建数据库备份
try
{
    var dir = Path.GetDirectoryName(wcDbPath);
    var fileName = Path.GetFileNameWithoutExtension(wcDbPath);
    var ext = Path.GetExtension(wcDbPath);
    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
    backupPath = Path.Combine(dir!, $"{fileName}_{timestamp}{ext}.bak");

    File.Copy(wcDbPath, backupPath, overwrite: false);
    Console.WriteLine($"数据库备份成功：{backupPath}");
}
catch (Exception ex)
{
    Console.WriteLine($"备份失败: {ex.Message}");
    Console.Write("是否继续执行? (y/n): ");
    var input = Console.ReadLine()?.Trim().ToLower();
    if (input != "y" && input != "yes")
    {
        Console.WriteLine("操作已取消");
        Console.ReadKey();
        return;
    }
}

// 执行清空操作
var operationSuccess = false;
try
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = wcDbPath }.ToString());
    connection.Open();
    Console.WriteLine("\n开始清空表数据...");

    foreach (var table in tableNames)
    {
        using var command = new SqliteCommand($"DELETE FROM {table};", connection);
        var rows = command.ExecuteNonQuery();
        Console.WriteLine($"表 {table} 清空成功，删除 {rows} 条记录");
    }

    operationSuccess = true;
    Console.WriteLine("\n表数据清空完成");
}
catch (Exception ex)
{
    Console.WriteLine($"数据库操作失败: {ex.Message}");
}

// 根据操作结果处理备份文件
if (operationSuccess)
{
    // 调用TortoiseSVN的Cleanup功能
    var cleanupSuccess = RunTortoiseSvnCleanup(svnRootDir);

    if (cleanupSuccess)
    {
        Console.WriteLine("TortoiseSVN Cleanup操作成功完成");
        HandleBackupAfterSuccess(wcDbPath);
    }
    else
    {
        Console.WriteLine("TortoiseSVN Cleanup操作失败");
        HandleBackupAfterFailure(wcDbPath);
    }
}
else
{
    HandleBackupAfterFailure(wcDbPath);
}

Console.WriteLine("\n按任意键退出...");
Console.ReadKey();

/// <summary>
/// 查找SVN根目录
/// </summary>
string? FindSvnRootDirectory(string startDir)
{
    var currentDir = startDir;
    var rootDir = Path.GetPathRoot(startDir);

    while (!string.IsNullOrEmpty(currentDir) && !currentDir.Equals(rootDir, StringComparison.OrdinalIgnoreCase))
    {
        var svnDir = Path.Combine(currentDir, ".svn");
        var wcDb = Path.Combine(svnDir, "wc.db");

        if (File.Exists(wcDb))
        {
            return currentDir; // 返回SVN根目录
        }

        currentDir = Path.GetDirectoryName(currentDir);
    }

    if (currentDir == rootDir)
    {
        var svnDir = Path.Combine(currentDir, ".svn");
        var wcDb = Path.Combine(svnDir, "wc.db");
        if (File.Exists(wcDb))
        {
            return currentDir;
        }
    }

    return null;
}

/// <summary>
/// 调用TortoiseSVN执行Cleanup操作
/// </summary>
bool RunTortoiseSvnCleanup(string workingCopyPath)
{
    try
    {
        // 查找TortoiseSVN的安装路径
        var tortoiseProcPath = GetTortoiseSvnPath();
        if (string.IsNullOrEmpty(tortoiseProcPath) || !File.Exists(tortoiseProcPath))
        {
            Console.WriteLine("未找到TortoiseSVN，请先安装TortoiseSVN");
            return false;
        }

        Console.WriteLine("\n正在执行TortoiseSVN Cleanup操作...");

        // 构建Cleanup命令参数
        // /command:cleanup - 执行cleanup命令
        // /path: - 指定工作副本路径
        // /nodialog - 不显示对话框，在后台执行
        var arguments = $"/command:cleanup /path:\"{workingCopyPath}\" /nodialog";

        // 启动进程
        var processInfo = new ProcessStartInfo
        {
            FileName = tortoiseProcPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(processInfo);
        process?.WaitForExit();

        // 检查退出代码（0表示成功）
        return process?.ExitCode == 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"执行Cleanup操作时出错: {ex.Message}");
        return false;
    }
}

/// <summary>
/// 从注册表获取TortoiseSVN的安装路径
/// </summary>
string? GetTortoiseSvnPath()
{
    try
    {
        // 检查32位注册表
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TortoiseSVN"))
        {
            if (key != null)
            {
                return key.GetValue("ProcPath") as string;
            }
        }

        // 检查64位注册表
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\TortoiseSVN"))
        {
            if (key != null)
            {
                return key.GetValue("ProcPath") as string;
            }
        }

        return null;
    }
    catch
    {
        return null;
    }
}

/// <summary>
/// 操作成功后处理备份文件
/// </summary>
void HandleBackupAfterSuccess(string originalPath)
{
    if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
    {
        Console.WriteLine("\n未找到备份文件，无需处理");
        return;
    }

    Console.Write("\n修复是否成功？(y=删除备份 / n=从备份还原): ");
    var input = Console.ReadLine()?.Trim().ToLower();

    if (input == "y" || input == "yes")
    {
        try
        {
            File.Delete(backupPath);
            Console.WriteLine($"备份文件已删除: {backupPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除备份失败: {ex.Message}");
            Console.WriteLine($"请手动删除备份文件: {backupPath}");
        }
    }
    else if (input == "n" || input == "no")
    {
        try
        {
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }
            File.Copy(backupPath, originalPath);
            File.Delete(backupPath);
            Console.WriteLine("已从备份还原原始数据库，操作已撤销");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"还原失败: {ex.Message}");
            Console.WriteLine($"请手动从备份恢复: {backupPath}");
        }
    }
    else
    {
        Console.WriteLine("输入无效，备份文件将保留");
        Console.WriteLine($"备份位置: {backupPath}");
    }
}

/// <summary>
/// 操作失败后处理备份文件
/// </summary>
void HandleBackupAfterFailure(string originalPath)
{
    if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
    {
        Console.WriteLine("\n未找到备份文件，无法还原");
        return;
    }

    Console.Write("\n是否从备份文件还原数据？(y/n): ");
    var input = Console.ReadLine()?.Trim().ToLower();

    if (input == "y" || input == "yes")
    {
        try
        {
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }
            File.Copy(backupPath, originalPath);
            File.Delete(backupPath);
            Console.WriteLine("已从备份文件还原数据");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"还原失败: {ex.Message}");
            Console.WriteLine($"请手动恢复: {backupPath}");
        }
    }
    else
    {
        Console.WriteLine($"备份文件已保留，位置: {backupPath}");
    }
}

/// <summary>
/// 安装右键菜单
/// </summary>
void InstallContextMenu()
{
    try
    {
        var appPath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        if (string.IsNullOrEmpty(appPath))
        {
            Console.WriteLine("无法获取程序路径，安装失败");
            return;
        }

        var menuName = "清空SVN工作队列";

        // 文件夹右键菜单
        using (var key = Registry.ClassesRoot.OpenSubKey(@"Directory\shell", writable: true)!)
        {
            var menuKey = key.CreateSubKey(menuName);
            menuKey.SetValue("", menuName);
            menuKey.SetValue("Icon", appPath);

            var cmdKey = menuKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appPath}\" \"%V\"");
        }

        // 文件夹空白处右键菜单
        using (var key = Registry.ClassesRoot.OpenSubKey(@"Directory\Background\shell", writable: true)!)
        {
            var menuKey = key.CreateSubKey(menuName);
            menuKey.SetValue("", menuName);
            menuKey.SetValue("Icon", appPath);

            var cmdKey = menuKey.CreateSubKey("command");
            cmdKey.SetValue("", $"\"{appPath}\" \"%V\"");
        }

        // .db文件右键菜单
        using (var key = Registry.ClassesRoot.OpenSubKey(@".db", writable: true))
        {
            if (key != null)
            {
                // 获取.db文件的默认打开方式（文件类型）
                var fileType = key.GetValue("") as string;
                if (!string.IsNullOrEmpty(fileType))
                {
                    using var shellKey = Registry.ClassesRoot.OpenSubKey($@"{fileType}\shell", writable: true);
                    if (shellKey != null)
                    {
                        var menuKey = shellKey.CreateSubKey(menuName);
                        menuKey.SetValue("",     menuName);
                        menuKey.SetValue("Icon", appPath);

                        var cmdKey = menuKey.CreateSubKey("command");
                        cmdKey.SetValue("", $"\"{appPath}\" \"%1\"");
                    }
                }
            }
        }

        Console.WriteLine("右键菜单安装成功！");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"安装失败: {ex.Message}");
        Console.WriteLine("请以管理员身份运行重试");
    }
}

/// <summary>
/// 卸载右键菜单
/// </summary>
void UninstallContextMenu()
{
    try
    {
        var menuName = "清空SVN工作队列";

        using (var key = Registry.ClassesRoot.OpenSubKey(@"Directory\shell", writable: true)!)
        {
            key.DeleteSubKeyTree(menuName, throwOnMissingSubKey: false);
        }

        using (var key = Registry.ClassesRoot.OpenSubKey(@"Directory\Background\shell", writable: true)!)
        {
            key.DeleteSubKeyTree(menuName, throwOnMissingSubKey: false);
        }

        // .db文件右键菜单卸载
        using (var key = Registry.ClassesRoot.OpenSubKey(@".db", writable: true))
        {
            if (key != null)
            {
                var fileType = key.GetValue("") as string;
                if (!string.IsNullOrEmpty(fileType))
                {
                    using var shellKey = Registry.ClassesRoot.OpenSubKey($@"{fileType}\shell", writable: true);
                    shellKey?.DeleteSubKeyTree(menuName, throwOnMissingSubKey: false);
                }
            }
        }

        Console.WriteLine("右键菜单卸载成功！");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"卸载失败: {ex.Message}");
        Console.WriteLine("请以管理员身份运行重试");
    }
}
