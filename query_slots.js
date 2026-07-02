const { MongoClient } = require('mongodb');
async function run() {
  const uri = "mongodb+srv://workbridgeanandhu:P%40ssword1@cluster0.dseaj.mongodb.net/TaskManagerDb?appName=Cluster0";
  const client = new MongoClient(uri);
  try {
    await client.connect();
    const db = client.db('Apenir');
    const slots = await db.collection('appointment_slots').find({}).toArray();
    console.log(`Total slots: ${slots.length}`);
    if (slots.length > 0) {
      console.log("Sample slots:");
      console.dir(slots.slice(0, 5), { depth: null });
    }
  } finally {
    await client.close();
  }
}
run().catch(console.dir);
