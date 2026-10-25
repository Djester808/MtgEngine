const {DatabaseSync}=require('node:sqlite');
const fs=require('node:fs');
const crypto=require('node:crypto');
const path=process.argv[2];
const db=new DatabaseSync(path,{readOnly:true});
try {
  const tables=db.prepare("SELECT name,sql FROM sqlite_schema WHERE type='table' ORDER BY name").all();
  const counts=Object.fromEntries(['Cards','Abilities','Prompts'].map(name=>[name,db.prepare(`SELECT count(*) AS n FROM "${name}"`).get().n]));
  const versions=db.prepare('SELECT Type,Version FROM Versions').all();
  const abilitySample=db.prepare('SELECT Id,BaseId,TextId,Category,SubCategory,FullyParsed,EmbeddedActiveAbilityIds,ReferencedAbilityIds,ModalChildIds,CostTypes FROM Abilities ORDER BY Id LIMIT 5').all();
  const result={kind:'local-readonly-sqlite-fallback',path,sha256:crypto.createHash('sha256').update(fs.readFileSync(path)).digest('hex'),tables,counts,versions,abilitySample};
  fs.writeFileSync(process.argv[3],JSON.stringify(result,null,2));
  process.stdout.write(JSON.stringify({tableCount:tables.length,counts,versions,abilitySample},null,2));
} finally {db.close();}
