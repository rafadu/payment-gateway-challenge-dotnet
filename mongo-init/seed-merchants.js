// Seeds the `merchants` collection for the payment gateway (ADR-0010). Mongo runs every *.js in
// /docker-entrypoint-initdb.d once, on first container startup, against MONGO_INITDB_DATABASE
// (payment_gateway). There is no merchant-registration endpoint by design — merchants are seeded.
//
// NOTE: `hashedSecret` below is a PLACEHOLDER. The real BCrypt hash of the demo secret is generated
// and dropped in here in stage 6 (when BCrypt is introduced), and the demo clientId/clientSecret are
// documented in the README in stage 9. Until then this proves the seed mechanism creates the
// document; it is not yet a credential you can authenticate with.

// Idempotent upsert ($setOnInsert only writes on first insert): this init script runs only on a
// fresh data dir today, but staying idempotent means it won't collide with the unique index if a
// persistent /data/db volume is added later and the script is ever re-run over existing data.
db.merchants.updateOne(
    { clientId: "demo-merchant" },
    {
        $setOnInsert: {
            merchantId: "11111111-1111-1111-1111-111111111111",
            clientId: "demo-merchant",
            hashedSecret: "PLACEHOLDER_REPLACED_WITH_BCRYPT_HASH_IN_STAGE_6",
            createdAt: new Date()
        }
    },
    { upsert: true }
);

// clientId is the lookup key for the token endpoint; enforce uniqueness.
db.merchants.createIndex({ clientId: 1 }, { unique: true });
