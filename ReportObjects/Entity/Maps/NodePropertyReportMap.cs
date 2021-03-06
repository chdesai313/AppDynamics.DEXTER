﻿using CsvHelper.Configuration;

namespace AppDynamics.Dexter.DataObjects
{
    public class NodePropertyReportMap : ClassMap<EntityNodeProperty>
    {
        public NodePropertyReportMap()
        {
            int i = 0; 
            Map(m => m.Controller).Index(i); i++;
            Map(m => m.ApplicationName).Index(i); i++;
            Map(m => m.TierName).Index(i); i++;
            Map(m => m.NodeName).Index(i); i++;
            Map(m => m.AgentType).Index(i); i++;

            Map(m => m.PropName).Index(i); i++;
            Map(m => m.PropValue).Index(i); i++;

            Map(m => m.ApplicationID).Index(i); i++;
            Map(m => m.TierID).Index(i); i++;
            Map(m => m.NodeID).Index(i); i++;
        }
    }
}
