using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

using CommandLine;

using Newtonsoft.Json;

using Onion.SolutionParser.Parser;
using Onion.SolutionParser.Parser.Model;
using ReQube.Models;
using ReQube.Models.ReSharper;
using ReQube.Models.SonarQube;

namespace ReQube
{
    internal static class Program
    {
        private static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        private static void Convert(Options options)
        {
            StreamReader reader = null;
            try
            {
                var serializer = new XmlSerializer(typeof(Report));

                Console.WriteLine("Reading input file {0}", options.Input);
                var solutionDirectoryDepth = GetSolutionDirectoryDepth(options.Input);
                reader = new StreamReader(options.Input);
                var report = (Report)serializer.Deserialize(reader);
                reader.Dispose();

                var sonarQubeReports = Map(report,solutionDirectoryDepth);

                // We need to write dummy report because SonarQube MSBuild reads a report from the root
                if (string.IsNullOrEmpty(options.Project))
                {
                    try
                    {
                        var solution = SolutionParser.Parse(report.Information.Solution);

                        WriteReport(CombineOutputPath(options, options.Output), SonarQubeReport.Empty);

                        foreach (var sonarQubeReport in sonarQubeReports)
                        {
                            var filePath = CombineOutputPath(options, Path.Combine(GetFolderProject(solution, sonarQubeReport, solutionDirectoryDepth), options.Output));
                            WriteReport(filePath, sonarQubeReport);
                        }

                        TryWriteMissingReports(solution, options, sonarQubeReports, solutionDirectoryDepth);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
                else
                {
                    var projectToWrite = sonarQubeReports.FirstOrDefault(r => string.Equals(r.ProjectName, options.Project, StringComparison.OrdinalIgnoreCase));
                    if (projectToWrite == null)
                    {
                        Console.WriteLine("Project " + options.Project + " not found or it contains no issues.");
                    }

                    WriteReport(CombineOutputPath(options, options.Output), projectToWrite ?? SonarQubeReport.Empty);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
            }
            finally
            {
                reader?.Dispose();
            }
        }

        private static int GetSolutionDirectoryDepth(string path)
        {
            return path.Split(@"\").Length - 2;
        }

        private static string GetFolderProject(ISolution solution, SonarQubeReport sonarQubeReport,int solutionDirectoryDepth)
        {
            var paths = solution.Projects
                .Where(x => x.Name == sonarQubeReport.ProjectName  && x.TypeGuid != Constants.ProjectTypeGuids["Solution Folder"])
                .Select(x => x.Path)
                .ToList();

            string path = null;

            if (paths.Count > 1)
            {
                //Si hay más de un resultado tenemos que tomar como referencia alguna de las incidencias para detectar que ruta es la que corresponde.
                var sampleIssue = sonarQubeReport.Issues.FirstOrDefault();
                if (sampleIssue != null)
                {
                    var rootPath = sampleIssue.PrimaryLocation.FilePath.Split(@"\").FirstOrDefault();
                    path = solution.Projects
                        .Where(x => x.Name == sonarQubeReport.ProjectName 
                                    && x.TypeGuid != Constants.ProjectTypeGuids["Solution Folder"] 
                                    && x.Path.Split(@"\").FirstOrDefault() == rootPath)
                        .Select(x => x.Path)
                        .FirstOrDefault();
                }
            }
            else
            {
                path = paths.FirstOrDefault();
            }

            if (path != null && solutionDirectoryDepth>0)
                path = RemoveUpperDirectories(path, solutionDirectoryDepth);

            return path != null ? Path.GetDirectoryName(path) : sonarQubeReport.ProjectName;
        }

        private static string CombineOutputPath(Options options, string directory)
        {
            return string.IsNullOrEmpty(options.Directory) ? directory : Path.Combine(options.Directory, directory);
        }

        private static void WriteReport(string filePath, SonarQubeReport sonarQubeReport)
        {
            Console.WriteLine("Writing output files {0}", filePath);

            var projectDirectory = Path.GetDirectoryName(filePath);
            if (projectDirectory != null && !Directory.Exists(projectDirectory))
            {
                Directory.CreateDirectory(projectDirectory);
            }

            File.WriteAllText(filePath, JsonConvert.SerializeObject(sonarQubeReport, JsonSerializerSettings));
        }

        private static void HandleParseError(IEnumerable<Error> errors)
        {
            Console.WriteLine("The following parsing errors occurred when parsing the solution file");
            foreach (var error in errors)
            {
                Console.WriteLine("Type {0} StopProcessing {1}", error.Tag, error.StopsProcessing);
            }
        }

        private static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Convert)
                .WithNotParsed(errors => HandleParseError(errors));
        }

        private static List<SonarQubeReport> Map(Report report, int solutionDirectoryDepth)
        {
            var reportIssueTypes = report.IssueTypes.ToDictionary(t => t.Id, type => type);

            var sonarQubeReports = new List<SonarQubeReport>();

            foreach (var project in report.Issues)
            {
                var sonarQubeReport = new SonarQubeReport { ProjectName = project.Name };
                var replaceFileNameRegex = new Regex($@"^{Regex.Escape(project.Name)}\\");
                foreach (var issue in project.Issue)
                {
                    if (!reportIssueTypes.TryGetValue(issue.TypeId, out ReportIssueType issueType))
                    {
                        Console.WriteLine("Unable to find issue type {0}.", issue.TypeId);

                        continue;
                    }

                    if (!Constants.ReSharperToSonarQubeSeverityMap.TryGetValue(issueType.Severity, out string sonarQubeSeverity))
                    {
                        Console.WriteLine("Unable to map ReSharper severity {0} to SonarQube", issueType.Severity);

                        continue;
                    }

                    var sonarQubeIssue = new Issue
                                             {
                                                 EngineId = Constants.EngineId,
                                                 RuleId = issue.TypeId,
                                                 Type = Constants.SonarQubeCodeSmellType,
                                                 Severity = sonarQubeSeverity,
                                                 PrimaryLocation =
                                                     new PrimaryLocation
                                                         {
                                                             FilePath = replaceFileNameRegex.Replace(issue.File, string.Empty),
                                                             Message = issue.Message,
                                                             TextRange =
                                                                 new TextRange
                                                                     {
                                                                         // For some reason, some issues doesn't have line, but actually they are on the first one
                                                                         StartLine = issue.Line > 0 ? issue.Line : 1
                                                                     }
                                                         }
                                             };

                    if (solutionDirectoryDepth>0)
                        sonarQubeIssue.PrimaryLocation.FilePath = RemoveUpperDirectories(sonarQubeIssue.PrimaryLocation.FilePath, solutionDirectoryDepth + 1);

                    sonarQubeReport.Issues.Add(sonarQubeIssue);
                }

                sonarQubeReports.Add(sonarQubeReport);
            }

            return sonarQubeReports;
        }

        private static string RemoveUpperDirectories(string filePath, int numDirectoriesToRemove)
        {
            var parts=filePath.Split(@"\");
            var result = string.Join(@"\",parts.Skip(numDirectoriesToRemove));
            return result;
        }

        private static void TryWriteMissingReports(ISolution solution, Options options, List<SonarQubeReport> sonarQubeReports, int solutionDirectoryDepth)
        {
            try
            {
                foreach (var project in solution.Projects)
                {
                    // We should skip solution directories
                    if (!project.Path.EndsWith(".csproj"))
                    {
                        continue;
                    }

                    if (sonarQubeReports.Any(r => string.Equals(r.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var projectPath = Path.GetDirectoryName(project.Path);
                    if (projectPath != null && solutionDirectoryDepth>0)
                        projectPath = RemoveUpperDirectories(projectPath, solutionDirectoryDepth);

                    var reportPath = CombineOutputPath(options, Path.Combine(projectPath, options.Output));
                    WriteReport(reportPath, SonarQubeReport.Empty);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}
