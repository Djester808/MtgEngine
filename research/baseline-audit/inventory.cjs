const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const root=path.resolve(__dirname,'../..');
const rel=p=>path.relative(root,p).replaceAll('\\','/');
function files(dir,ext){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(d=>['bin','obj','.git'].includes(d.name)?[]:d.isDirectory()?files(path.join(dir,d.name),ext):d.name.endsWith(ext)?[path.join(dir,d.name)]:[]);}
const sourceRoots=['MtgEngine.Cards','MtgEngine.Rules','MtgEngine.Cards.Pipeline','MtgEngine.Api'];
const refs=[];const testMethods=[];
for(const dir of [...sourceRoots,'tests'])for(const file of files(path.join(root,dir),'.cs')){
 const text=fs.readFileSync(file,'utf8');let line=1;let testAttr=false;
 for(const value of text.split(/\r?\n/)){
  for(const m of value.matchAll(/RuleRef\("([0-9]{3}(?:\.[0-9]+[a-z]?)?)"/g))refs.push({rule:m[1],file:rel(file),line,test:dir==='tests'});
  if(/\[(?:Fact|Theory)(?:\(|\])/.test(value))testAttr=true;
  const method=value.match(/public\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+(\w+)\s*\(/);
  if(testAttr&&method){testMethods.push({file:rel(file),line,method:method[1]});testAttr=false;}
  line++;
 }
}
const ruleFile=path.join(root,'MtgEngine.Api/Knowledge/comprehensive-rules.txt');
const ruleText=fs.readFileSync(ruleFile,'utf8');
const headings=new Map();const clauses=new Map();
let line=1;
for(const value of ruleText.split(/\r?\n/)){
 const h=value.match(/^(\d{3})\.\s+(.+)$/);if(h)headings.set(h[1],{id:h[1],title:h[2],line});
 const m=value.match(/^(\d{3}\.\d+[a-z]?)\.?\s+(.+)$/);
 if(m)clauses.set(m[1],{id:m[1],line,summary:m[2].slice(0,180),status:'Unassessed individually'});
 line++;
}
const register=[...clauses.values()].map(c=>({...c,referenceCandidates:refs.filter(r=>r.rule===c.id).map(r=>({file:r.file,line:r.line,test:r.test}))}));
const groups=[...headings.values()].filter(h=>h.line>150).map(h=>({...h,clauseCount:register.filter(c=>c.id.startsWith(h.id+'.')).length,sourceReferenceCount:refs.filter(r=>!r.test&&(r.rule===h.id||r.rule.startsWith(h.id+'.'))).length,testReferenceCount:refs.filter(r=>r.test&&(r.rule===h.id||r.rule.startsWith(h.id+'.'))).length}));
const keywordSections=[...clauses.values()].filter(c=>/^70[12]\.\d+$/.test(c.id));
const cardRoot=path.join(root,'cards');
const cards=fs.readdirSync(cardRoot,{withFileTypes:true}).filter(d=>d.isFile()&&d.name.endsWith('.json')).map(d=>path.join(cardRoot,d.name)).map(file=>{try{const c=JSON.parse(fs.readFileSync(file,'utf8'));return {file:rel(file),name:c.name,id:c.oracleId,status:c.status,storedScenarios:(c.scenarios||[]).length,verification:c.verification||null};}catch(e){return {file:rel(file),error:String(e)}}});
const countBy=(xs,key)=>xs.reduce((m,x)=>(m[x[key]??'missing']=(m[x[key]??'missing']||0)+1,m),{});
const manifest={date:'2026-10-21',branch:'card-engine',rulesEffective:'2026-08-07',rulesSha256:crypto.createHash('sha256').update(ruleText).digest('hex'),ruleClauses:register.length,primaryGroups:groups.length,keywordAndActionSections:keywordSections.length,sourceRuleReferences:refs.filter(r=>!r.test).length,testRuleReferences:refs.filter(r=>r.test).length,declaredTestMethods:testMethods.length,cardFiles:cards.length,statusCounts:countBy(cards,'status'),storedScenarios:cards.reduce((n,c)=>n+(c.storedScenarios||0),0)};
for(const [name,value]of Object.entries({'rule-register.json':register,'rule-groups.json':groups,'rule-reference-index.json':refs,'test-source-index.json':testMethods,'card-inventory.json':cards,'inventory-summary.json':manifest}))fs.writeFileSync(path.join(__dirname,name),JSON.stringify(value,null,2));
let md='# Complete numbered rules register\n\nPinned to the repository rules effective August 7, 2026. Every numbered clause is indexed in [rule-register.json](rule-register.json), with its source line and exact attribute-reference candidates. All clauses remain individually unassessed unless the audit report identifies a demonstrated case. Citations, source presence and passing nearby tests are not proof of conformance. No rules coverage percentage is inferred.\n\n## Primary rule groups\n\n| Group | Subject | Clauses | Source citations | Test citations | Individual conformance |\n| --- | --- | ---: | ---: | ---: | --- |\n';
for(const g of groups)md+=`| ${g.id} | ${g.title.replaceAll('|','/')} | ${g.clauseCount} | ${g.sourceReferenceCount} | ${g.testReferenceCount} | Unassessed except specific audit cases |\n`;
md+='\n## Every keyword ability and keyword action section\n\nThese entries ensure that the long tail of mechanics is registered rather than implied complete by a handful of examples. The source and tests may implement part of each section; see the JSON index and subsystem report.\n\n| Section | Rule heading | Individual conformance |\n| --- | --- | --- |\n';
for(const k of keywordSections)md+=`| ${k.id} | ${k.summary.replaceAll('|','/')} | Unassessed except specific audit cases |\n`;
fs.writeFileSync(path.join(__dirname,'RULES_REGISTER.md'),md);
console.log(JSON.stringify(manifest,null,2));
