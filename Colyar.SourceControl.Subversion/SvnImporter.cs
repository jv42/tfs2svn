using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Colyar.SourceControl.Subversion
{
    public class SvnImporter
    {
        #region Private Variables

        private string _repositoryPath;
        private string _workingCopyPath;
        private readonly string _svnPath;
        private readonly Dictionary<string, string> _usernameMap = new Dictionary<string, string>();
        private static readonly ILog log = LogManager.GetLogger(typeof(SvnImporter));

        #endregion

        #region Public Properties

        public string WorkingCopyPath
        {
            get { return this._workingCopyPath; }
        }

        public string RepositoryPath
        {
            get { return this._repositoryPath; }
        }

        #endregion

        #region Public Constructor

        public SvnImporter(string repositoryPath, string workingCopyPath, string svnBinFolder)
        {
            this._repositoryPath = repositoryPath.Replace("\\", "/");
            this._workingCopyPath = workingCopyPath;
            this._svnPath = svnBinFolder;
        }

        #endregion

        #region Public Methods

        public void CreateRepository(string repositoryPath)
        {
            RunSvnAdminCommand(String.Format("create \"{0}\"", repositoryPath));
        }
        public void CreateRepository()
        {
            CreateRepository(this._repositoryPath);
        }
        public void Checkout(string repositoryPath, string workingCopyPath)
        {
            this._repositoryPath = repositoryPath;
            this._workingCopyPath = workingCopyPath;

            Checkout();
        }
        public void Checkout()
        {
            RunSvnCommand(String.Format("co \"{0}\" \"{1}\"", this._repositoryPath, this._workingCopyPath));
        }
        public void Update()
        {
            RunSvnCommand(String.Format("up \"{0}\"", this._workingCopyPath));
        }
        public void Commit(string message, string committer, DateTime commitDate, int changeSet)
        {
            // clean-up message for svn and remove non-ASCII chars
            if (message != null)
            {
                message = message.Replace("\"", "\\\"").Replace("\r\n", "\n");
                // http://svnbook.red-bean.com/en/1.2/svn.advanced.l10n.html
                message = Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(message));
            }

            message = String.Format("[TFS Changeset #{0}]\n{1}",
                changeSet.ToString(CultureInfo.InvariantCulture),
                message);

            RunSvnCommand(String.Format("commit \"{0}\" -m \"{1}\"",
                this._workingCopyPath,
                message));

            SetCommitAuthorAndDate(commitDate, committer);
        }
        public void Add(string path)
        {
            if (path != this._workingCopyPath)
            {
                AddMissingDirectoryIfNeeded(path);
                RunSvnCommand(String.Format("add \"{0}\"", path));
            }
        }
        public void AddFolder(string path)
        {
            if (path != this._workingCopyPath)
            {
                AddMissingDirectoryIfNeeded(path);
                RunSvnCommand(String.Format("add --depth=empty \"{0}\"", path));
            }
        }

        public void Remove(string path, bool isFolder)
        {
            RunSvnCommand(String.Format("rm \"{0}\"", path));

            if (isFolder)
                RunSvnCommand(String.Format("up \"{0}\"", path));
        }

        /// <summary>
        /// Cleanup a path.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="isFolder"></param>
        public void CleanUp(string path)
        {
            RunSvnCommand(String.Format("cleanup \"{0}\"", path));
        }

        /// <summary>
        /// Force removal of a path.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="isFolder"></param>
        public void ForceRemove(string path, bool isFolder)
        {
            RunSvnCommand(String.Format("rm --force \"{0}\"", path));

            if (isFolder)
                RunSvnCommand(String.Format("up \"{0}\"", path));
        }

        public void MoveFile(string oldPath, string newPath, bool isFolder)
        {
            AddMissingDirectoryIfNeeded(newPath);
            RunSvnCommand(String.Format("mv \"{0}\" \"{1}\"", oldPath, newPath));
        }
        public void MoveServerSide(string oldPath, string newPath, int changeset, string committer, DateTime commitDate)
        {
            string oldUrl = _repositoryPath + ToUrlPath(oldPath.Remove(0, _workingCopyPath.Length));
            string newUrl = _repositoryPath + ToUrlPath(newPath.Remove(0, _workingCopyPath.Length));

            //when only casing is different, we need a server-side move/rename (because windows is case unsensitive!)

            RunSvnCommand(String.Format("mv \"{0}\" \"{1}\" --message \"[TFS Changeset #{2}]\ntfs2svn: server-side rename\"", oldUrl, newUrl, changeset));
            Update(); //todo: only update common rootpath of oldPath and newPath?

            SetCommitAuthorAndDate(commitDate, committer);
        }

        public void AddUsernameMapping(string tfsUsername, string svnUsername)
        {
            this._usernameMap[tfsUsername] = svnUsername;
        }
        #endregion

        #region Private Methods

        private void AddMissingDirectoryIfNeeded(string path)
        {
            string directory = Directory.GetParent(path).FullName;

            if (Directory.Exists(directory))
                return;

            log.Info("Adding: " + directory);
            Directory.CreateDirectory(directory);
            string workingCopyDirectory;
            if (!_workingCopyPath.EndsWith("\\"))
            {
                workingCopyDirectory = Directory.GetParent(_workingCopyPath + '\\').FullName;
            }
            else
            {
                workingCopyDirectory = Directory.GetParent(_workingCopyPath).FullName;
            }

            string[] pathParts = directory.Substring(workingCopyDirectory.Length).Split('\\');
            foreach (string pathPart in pathParts)
            {
                workingCopyDirectory += '\\';
                workingCopyDirectory += pathPart;
                RunSvnCommand(String.Format("add --depth=empty {0}", workingCopyDirectory));
            }

            //RunSvnCommand("add --force \"" + directory + "\"");
            // Commit("Adding missing directory", "tfs2svn", DateTime.Today, 0);
        }
        private void SetCommitAuthorAndDate(DateTime commitDate, string committer)
        {
            string username = GetMappedUsername(committer);

            //set time after commit
            RunSvnCommand(String.Format("propset svn:date --revprop -rHEAD {0} \"{1}\"",
                commitDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
                this._workingCopyPath));

            RunSvnCommand(String.Format("propset svn:author --revprop -rHEAD \"{0}\" \"{1}\"",
                username,
                this._workingCopyPath));
        }
        private string ToUrlPath(string path)
        {
            return path.Replace("\\", "/");
        }
        private Tuple<string, string> RunCommand(string executablePath, string arguments)
        {
            string standardOutput;
            string errorOutput;
            Process p = new Process();
            p.StartInfo.FileName = executablePath;
            p.StartInfo.Arguments = arguments;
            // Redirect the output stream of the child process.
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;

            p.Start();
            /*if (!p.HasExited)
            {
                p.PriorityClass = ProcessPriorityClass.High;
            }*/
            standardOutput = p.StandardOutput.ReadToEnd();
            errorOutput = p.StandardError.ReadToEnd();
            if (!p.HasExited)
            {
                p.WaitForExit();
            }
            p.Close();

            return new Tuple<string, string>(standardOutput, errorOutput);
        }
        private void RunSvnCommand(string command)
        {
            log.Info("svn " + command);

            Tuple<string, string> outputs = RunCommand(this._svnPath + @"\svn.exe", command);

            ParseSvnOuput(command, outputs.Item2);// item2 is error output
        }
        private void RunSvnAdminCommand(string command)
        {
            log.Info("svnadmin " + command);

            Tuple<string, string> outputs = RunCommand(this._svnPath + @"\svnadmin.exe", command);

            ParseSvnOuput(command, outputs.Item2);// item2 is error output

        }

        private void ParseSvnOuput(string input, string output)
        {
            if (Regex.Match(output, "^svn: warning").Success || Regex.Match(output, "^svn: avertissement").Success)
            {
                log.Warn("Warning: " + output);
                return;
            }
            if (output != "")
            {
                throw new Exception(String.Format("svn error when executing 'svn {0}'. Exception: {1}.", input, output));
            }
        }

        private void ParseSvnAdminOuput(string input, string output)
        {
            if (output != "")
            {
                throw new Exception(String.Format("svn error when executing 'svn {0}'. Exception: {1}.", input, output));
            }
        }

        private string GetMappedUsername(string committer)
        {
            foreach (string tfsUsername in _usernameMap.Keys)
                if (committer.ToLowerInvariant().Contains(tfsUsername.ToLowerInvariant()))
                    return _usernameMap[tfsUsername];

            return committer; //no mapping found, return committer's unmapped name
        }

        #endregion
    }
}
