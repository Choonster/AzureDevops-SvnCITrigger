using Microsoft.TeamFoundation.Build.WebApi;
using System.Collections.Generic;

namespace SvnCITrigger
{
    public class RequiredBuildDetails
    {
        public BuildDefinition BuildDef { get; set; }
        public List<BranchInfo> Branches { get; set; } = new List<BranchInfo>();
    }
}
