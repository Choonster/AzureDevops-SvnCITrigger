using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using SharpSvn;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SvnCITrigger
{
    public class AzureSubversionCiController
    {
        internal string collectionUri = "";
        internal string projectName = "";
        internal string pat = "";
        internal string svnuser = "";
        internal string svnpasssword = "";

        public AzureSubversionCiController(string ProjectName, string svnUser, string svnPassword, string DevOpsUrl = "", string PAT = "")
        {
            projectName = ProjectName;
            svnuser = svnUser;
            svnpasssword = svnPassword;

            if (!string.IsNullOrEmpty(DevOpsUrl))
                collectionUri = DevOpsUrl;

            if (!string.IsNullOrEmpty(PAT))
                pat = PAT;

        }

        public bool RunCI()
        {

            VssConnection connection = new VssConnection(new Uri(collectionUri), new VssBasicCredential(string.Empty, pat));

            BuildHttpClient buildClient = connection.GetClient<BuildHttpClient>();

            Task<List<BuildDefinitionReference>> buildDefs = Task.Run(() => buildClient.GetDefinitionsAsync(projectName, null, null, null, null, null, null, null, null, null, null, null));
            buildDefs.Wait();

            List<BuildDefinition> buildDefinitions = new List<BuildDefinition>();

            foreach (BuildDefinitionReference bdr in buildDefs.Result)
            {
                Task<BuildDefinition> bd = Task.Run(() => buildClient.GetDefinitionAsync(projectName, bdr.Id));
                bd.Wait();

                buildDefinitions.Add(bd.Result);

            }

            SortedList<int, RequiredBuildDetails> buildIsNeeded = new SortedList<int, RequiredBuildDetails>();


            foreach (BuildDefinition buildDef in buildDefinitions)
            {
                bool ignoreDefintion = true;

                if (buildDef.Repository != null && buildDef.Repository.Type == "Svn")
                {
                    // get last version from the repository

                    int buildOrder = 0;
                    if (buildDef.Variables.ContainsKey("buildOrder"))
                    {
                        BuildDefinitionVariable bvBuildOrder = null;
                        if (buildDef.Variables.TryGetValue("buildOrder", out bvBuildOrder))
                        {
                            buildOrder = Convert.ToInt32(bvBuildOrder.Value);
                        }
                    }

                    if (buildDef.Variables.ContainsKey("buildCI"))
                    {
                        BuildDefinitionVariable bvBuildCi = null;
                        if (buildDef.Variables.TryGetValue("buildCI", out bvBuildCi))
                        {
                            ignoreDefintion = !Convert.ToBoolean(bvBuildCi.Value);
                        }
                    }

                    if (!ignoreDefintion)
                    {
                        Uri serverUrl = buildDef.Repository.Url;

                        if (!serverUrl.AbsolutePath.EndsWith("/")) // Add a trailing slash if the URL doesn't end with one
                        {
                            UriBuilder builder = new UriBuilder(serverUrl);
                            builder.Path += "/";
                            serverUrl = builder.Uri;
                        }

                        ContinuousIntegrationTrigger ciTrigger = buildDef.Triggers.OfType<ContinuousIntegrationTrigger>().FirstOrDefault();

                        List<string> branches;

                        if (ciTrigger?.PathFilters == null || !ciTrigger.PathFilters.Any())
                        {
                            branches = new List<string> { buildDef.Repository.DefaultBranch };
                        }
                        else
                        {
                            branches = GetBranches(serverUrl, ciTrigger.PathFilters);
                        }

                        RequiredBuildDetails requiredBuildDetails = new RequiredBuildDetails
                        {
                            BuildDef = buildDef
                        };

                        // get last version from builds
                        Task<List<Build>> taskBuildList = Task.Run(() => buildClient.GetBuildsAsync(projectName, new List<int> { buildDef.Id }));
                        taskBuildList.Wait();

                        foreach (string branch in branches)
                        {
                            long lastRepositoryVersion = GetLatestCheckinVersion(serverUrl, branch);

                            Build latestBuild = taskBuildList.Result.FirstOrDefault(b => b.SourceBranch == branch);
                            long lastBuildVersion = Convert.ToInt64(latestBuild?.SourceVersion);

                            if (lastBuildVersion < lastRepositoryVersion)
                            {
                                Console.WriteLine($"Build is required for {buildDef.Name} (branch {branch}) - last built version {lastBuildVersion} last checkin version {lastRepositoryVersion}");
                                requiredBuildDetails.Branches.Add(new BranchInfo
                                {
                                    Path = branch,
                                    LastRepositoryVersion = lastRepositoryVersion,
                                });
                            }
                            else
                            {
                                Console.WriteLine($"Build is up to date for {buildDef.Name} (branch {branch}) - last built version {lastBuildVersion} last checkin version {lastRepositoryVersion}");
                            }
                        }

                        if (requiredBuildDetails.Branches.Any())
                        {
                            buildIsNeeded.Add(buildOrder, requiredBuildDetails);
                        }
                    }
                }
            }

            // now we know what needs to be build, we must trigger any builds...
            foreach (RequiredBuildDetails requiredBuild in buildIsNeeded.Values)
            {
                BuildDefinition buildDef = requiredBuild.BuildDef;

                foreach (BranchInfo branch in requiredBuild.Branches)
                {
                    Console.WriteLine($"Triggering build for {buildDef.Name} (branch {branch.Path})");

                    Build build = new Build()
                    {
                        Definition = buildDef,
                        Project = buildDef.Project,
                        Reason = BuildReason.IndividualCI,
                        SourceBranch = branch.Path,
                        SourceVersion = branch.LastRepositoryVersion.ToString()
                    };

                    Task<Build> taskBuild = Task.Run(() => buildClient.QueueBuildAsync(build));
                    taskBuild.Wait();
                }

                return true; // ONLY ALLOW BUILDS FROM **ONE** BUILD DEFINITION PER TIME
            }

            return false;
        }

        private List<string> GetBranches(Uri serverUrl, List<string> pathFilters)
        {
            try
            {
                using (SvnClient client = CreateSvnClient())
                {
                    List<string> branches = new List<string>();

                    foreach (string pathFilter in pathFilters.Where(filter => filter[0] == '+'))
                    {
                        string filter = pathFilter.Substring(1); // Remove the + from the start
                        bool isDirectoryFilter = filter.EndsWith("/*") || filter.EndsWith("/");

                        if (isDirectoryFilter)
                        {
                            string baseDirectory = filter.TrimEnd('*');
                            client.GetList(SvnTarget.FromUri(new Uri(serverUrl, baseDirectory)), out Collection<SvnListEventArgs> svnList);

                            IEnumerable<string> relativeDirectoryUris = svnList
                                .Where(arg => arg.Uri != arg.BaseUri) // Exclude the parent directory
                                .Where(arg => arg.Entry.NodeKind == SvnNodeKind.Directory) // Only include child directories
                                .Select(arg => serverUrl.MakeRelativeUri(arg.Uri)) // Get the URI of each directory relative to serverUrl
                                .Select(uri => uri.ToString().TrimEnd('/')); // Convert each URI to a string and remove the trailing slash

                            branches.AddRange(relativeDirectoryUris);
                        }
                        else
                        {
                            branches.Add(filter);
                        }
                    }

                    foreach (string pathFilter in pathFilters.Where(filter => filter[0] == '-'))
                    {
                        string filter = pathFilter.Substring(1); // Remove the - from the start
                        bool isDirectoryFilter = filter.EndsWith("/*") || filter.EndsWith("/");

                        if (!isDirectoryFilter) // Ignore directory excludes
                        {
                            branches.Remove(filter);
                        }
                    }

                    return branches;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION - " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return null;
            }
        }

        private long GetLatestCheckinVersion(Uri serverUrl, string branch)
        {
            try
            {
                using (SvnClient client = CreateSvnClient())
                {
                    Uri endpoint = new Uri(serverUrl, branch);

                    client.GetInfo(endpoint, out SvnInfoEventArgs info);
                    long lastRevision = info.LastChangeRevision;

                    return lastRevision;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION - " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return -1;
            }
        }

        private SvnClient CreateSvnClient()
        {
            SvnClient client = new SvnClient();
            client.Authentication.Clear(); // Clear a previous authentication
            client.Authentication.DefaultCredentials = new System.Net.NetworkCredential(svnuser, svnpasssword);
            client.Authentication.SslServerTrustHandlers += Authentication_SslServerTrustHandlers;
            return client;
        }

        private void Authentication_SslServerTrustHandlers(object sender, SharpSvn.Security.SvnSslServerTrustEventArgs e)
        {
            e.AcceptedFailures = e.Failures;
            e.Save = true;
        }
    }
}
