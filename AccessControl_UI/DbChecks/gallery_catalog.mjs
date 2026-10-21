import {readFile} from 'node:fs/promises';
const {PGlite}=await import(process.env.PGLITE_MODULE || '@electric-sql/pglite');
const db=new PGlite();
await db.exec(`CREATE SCHEMA access_control;
CREATE TABLE access_control.persons(person_id uuid PRIMARY KEY,employee_no text,name text,is_active bool,access_enabled bool);
CREATE TABLE access_control.face_enrollments(enrollment_id uuid PRIMARY KEY,person_id uuid,is_active bool,model_version text,embedding_dimension int);`);
const model='arcface512_df093_dlibfast480_eyealign_v1';
const uuid=i=>'00000000-0000-0000-0000-'+String(i).padStart(12,'0');
for(let i=1;i<=6;i++){
 await db.query('INSERT INTO access_control.persons VALUES($1,$2,$3,$4,$5)',[uuid(i),'EMP'+i,'직원'+i,i!==2,i!==3]);
 if(i!==4)await db.query('INSERT INTO access_control.face_enrollments VALUES($1,$2,$3,$4,512)',[uuid(i+10),uuid(i),i!==5,i===6?'old-model':model]);
}
const source=await readFile(new URL('../AccessControlServer/FaceDatabase.cs',import.meta.url),'utf8');
const method=source.slice(source.indexOf('public async Task<List<FaceEmployee>> CatalogAsync'));
const sql=method.match(/new\s+NpgsqlCommand\s*\(\s*"([^"]+)"/)[1].replace('@model','$1');
const rows=(await db.query(sql,[model])).rows;
if(rows.length!==3 || !rows.some(r=>r.person_id===uuid(2)&&!r.is_active) || !rows.some(r=>r.person_id===uuid(3)&&!r.access_enabled))throw new Error('Catalog omitted employees needed for central denial or included invalid registrations');
console.log('Production catalog SQL: all compatible registered employees, inactive/no-permission retained for central denial, unregistered/old-model/inactive-enrollment excluded: PASS');
await db.close();
