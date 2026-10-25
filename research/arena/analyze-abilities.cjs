const {DatabaseSync}=require('node:sqlite');
const fs=require('node:fs');
const db=new DatabaseSync(process.argv[2],{readOnly:true});
try {
 const query=(sql)=>db.prepare(sql).all();
 const result={
  source:'card-database.sqlite snapshot; see database-schema.json for its hash',
  categories:query('SELECT Category,SubCategory,COUNT(*) AS Records FROM Abilities GROUP BY Category,SubCategory ORDER BY Records DESC'),
  parsedStatus:query('SELECT FullyParsed,COUNT(*) AS Records FROM Abilities GROUP BY FullyParsed'),
  sharedBaseFamilies:query('SELECT BaseId,COUNT(*) AS Records,COUNT(DISTINCT BaseIdNumeral) AS DistinctNumerals FROM Abilities WHERE BaseId<>0 GROUP BY BaseId ORDER BY Records DESC LIMIT 12'),
  familyLabels:query("SELECT a.Id,l.Loc FROM Abilities a JOIN Localizations_enUS l ON a.TextId=l.LocId WHERE a.Id IN (166,35,5,30) AND l.Formatted=0"),
  relationshipCoverage:query(`SELECT COUNT(*) AS Total,
   SUM(CASE WHEN BaseId<>0 THEN 1 ELSE 0 END) AS WithBase,
   SUM(CASE WHEN ReferencedAbilityIds IS NOT NULL AND ReferencedAbilityIds NOT IN ('','[]') THEN 1 ELSE 0 END) AS WithReferences,
   SUM(CASE WHEN HiddenAbilityIds IS NOT NULL AND HiddenAbilityIds NOT IN ('','[]') THEN 1 ELSE 0 END) AS WithHidden,
   SUM(CASE WHEN ModalChildIds IS NOT NULL AND ModalChildIds NOT IN ('','[]') THEN 1 ELSE 0 END) AS WithModalChildren
   FROM Abilities`),
  parameterExamples:query('SELECT Id,BaseId,BaseIdNumeral,Category,SubCategory,TextId,CostTypes,Tags FROM Abilities WHERE BaseId<>0 AND BaseIdNumeral IS NOT NULL ORDER BY Id LIMIT 10'),
  modalExamples:query("SELECT Id,BaseId,ModalChildIds,ModalParentIds,ReferencedAbilityIds,CostTypes,Tags FROM Abilities WHERE ModalChildIds IS NOT NULL AND ModalChildIds NOT IN ('','[]') ORDER BY Id LIMIT 3"),
  cardExamples:query("SELECT GrpId,TitleId,OldSchoolManaText,AbilityIds,HiddenAbilityIds,LinkedFaceGrpIds FROM Cards ORDER BY GrpId LIMIT 3")
 };
 fs.writeFileSync(process.argv[3],JSON.stringify(result,null,2));
 process.stdout.write(JSON.stringify({parsedStatus:result.parsedStatus,relationshipCoverage:result.relationshipCoverage,sharedBaseFamilies:result.sharedBaseFamilies.slice(0,4),parameterExamples:result.parameterExamples.slice(0,3),modalExamples:result.modalExamples},null,2));
} finally {db.close();}
