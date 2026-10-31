namespace Architector.Editor
{
    public static class ArchitectorAiGateway
    {
        public static ArchitectorCanonicalBatchResult DryRun(ArchitectorCanonicalBatch batch)
        {
            return ArchitectorSession.Instance.Repository.DryRunCanonicalBatch(batch);
        }

        public static ArchitectorCanonicalBatchResult Apply(ArchitectorCanonicalBatch batch)
        {
            return ArchitectorSession.Instance.Repository.ApplyCanonicalBatch(batch);
        }

        public static ArchitectorSemanticPlanResult DryRunPlan(
            ArchitectorSemanticPlan plan,
            string actionName = "Apply Semantic Plan")
        {
            var session = ArchitectorSession.Instance;
            return session.Repository.DryRunSemanticPlan(plan, actionName, session.TypeIndex);
        }

        public static ArchitectorSemanticPlanResult ApplyPlan(
            ArchitectorSemanticPlan plan,
            string actionName = "Apply Semantic Plan")
        {
            var session = ArchitectorSession.Instance;
            return session.Repository.ApplySemanticPlan(plan, actionName, session.TypeIndex);
        }

        public static ArchitectorCanonicalBatchResult ValidateExternalChanges()
        {
            return ArchitectorSession.Instance.Repository.ValidateExternalChanges();
        }

        public static ArchitectorCanonicalBatchResult ReloadExternalChanges()
        {
            return ArchitectorSession.Instance.Repository.ReloadExternalChanges();
        }
    }
}
