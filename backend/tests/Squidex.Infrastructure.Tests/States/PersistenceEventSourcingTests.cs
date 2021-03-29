﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschränkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FakeItEasy;
using Squidex.Infrastructure.EventSourcing;
using Squidex.Infrastructure.TestHelpers;
using Xunit;

namespace Squidex.Infrastructure.States
{
    public class PersistenceEventSourcingTests
    {
        private readonly DomainId key = DomainId.NewGuid();
        private readonly ISnapshotStore<int> snapshotStore = A.Fake<ISnapshotStore<int>>();
        private readonly IEventDataFormatter eventDataFormatter = A.Fake<IEventDataFormatter>();
        private readonly IEventStore eventStore = A.Fake<IEventStore>();
        private readonly IStreamNameResolver streamNameResolver = A.Fake<IStreamNameResolver>();
        private readonly IStore<int> sut;

        public PersistenceEventSourcingTests()
        {
            A.CallTo(() => streamNameResolver.GetStreamName(None.Type, A<string>._))
                .ReturnsLazily(x => x.GetArgument<string>(1)!);

            sut = new Store<int>(snapshotStore, eventStore, eventDataFormatter, streamNameResolver);
        }

        [Fact]
        public async Task Should_read_from_store()
        {
            var event1 = new MyEvent { MyProperty = "event1" };
            var event2 = new MyEvent { MyProperty = "event2" };

            SetupEventStore(event1, event2);

            var persistedEvents = Save.Events();
            var persistence = sut.WithEventSourcing(None.Type, key, persistedEvents.Write);

            await persistence.ReadAsync();

            Assert.Equal(persistedEvents.ToArray(), new[] { event1, event2 });
        }

        [Fact]
        public async Task Should_read_until_stopped()
        {
            var event1 = new MyEvent { MyProperty = "event1" };
            var event2 = new MyEvent { MyProperty = "event2" };

            SetupEventStore(event1, event2);

            var persistedEvents = Save.Events(1);
            var persistence = sut.WithEventSourcing(None.Type, key, persistedEvents.Write);

            await persistence.ReadAsync();

            Assert.Equal(persistedEvents.ToArray(), new[] { event1 });
        }

        [Fact]
        public async Task Should_ignore_old_events()
        {
            var storedEvent = new StoredEvent("1", "1", 0, new EventData("Type", new EnvelopeHeaders(), "Payload"));

            A.CallTo(() => eventStore.QueryAsync(key.ToString(), 0))
                .Returns(new List<StoredEvent> { storedEvent });

            A.CallTo(() => eventDataFormatter.ParseIfKnown(storedEvent))
                .Returns(null);

            var persistedEvents = Save.Events();
            var persistence = sut.WithEventSourcing(None.Type, key, persistedEvents.Write);

            await persistence.ReadAsync();

            Assert.Empty(persistedEvents);
            Assert.Equal(0, persistence.Version);
        }

        [Fact]
        public async Task Should_read_status_from_snapshot()
        {
            A.CallTo(() => snapshotStore.ReadAsync(key))
                .Returns((2, 2L));

            SetupEventStore(3, 2);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await persistence.ReadAsync();

            A.CallTo(() => eventStore.QueryAsync(key.ToString(), 3))
                .MustHaveHappened();
        }

        [Fact]
        public async Task Should_throw_exception_if_events_are_older_than_snapshot()
        {
            A.CallTo(() => snapshotStore.ReadAsync(key))
                .Returns((2, 2L));

            SetupEventStore(3, 0, 3);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.ReadAsync());
        }

        [Fact]
        public async Task Should_throw_exception_if_events_have_gaps_to_snapshot()
        {
            A.CallTo(() => snapshotStore.ReadAsync(key))
                .Returns((2, 2L));

            SetupEventStore(3, 4, 3);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.ReadAsync());
        }

        [Fact]
        public async Task Should_throw_exception_if_not_found()
        {
            SetupEventStore(0);

            var persistedEvents = Save.Events();
            var persistence = sut.WithEventSourcing(None.Type, key, persistedEvents.Write);

            await Assert.ThrowsAsync<DomainObjectNotFoundException>(() => persistence.ReadAsync(1));
        }

        [Fact]
        public async Task Should_throw_exception_if_other_version_found()
        {
            SetupEventStore(3);

            var persistedEvents = Save.Events();
            var persistence = sut.WithEventSourcing(None.Type, key, persistedEvents.Write);

            await Assert.ThrowsAsync<InconsistentStateException>(() => persistence.ReadAsync(1));
        }

        [Fact]
        public async Task Should_throw_exception_if_other_version_found_from_snapshot()
        {
            A.CallTo(() => snapshotStore.ReadAsync(key))
                .Returns((2, 2L));

            SetupEventStore(0);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await Assert.ThrowsAsync<InconsistentStateException>(() => persistence.ReadAsync(1));
        }

        [Fact]
        public async Task Should_not_throw_exception_if_nothing_expected()
        {
            SetupEventStore(0);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await persistence.ReadAsync();
        }

        [Fact]
        public async Task Should_write_events_to_store()
        {
            SetupEventStore(3);

            var persistedEvents = Save.Events();
            var persistence = sut.WithEventSourcing(None.Type, key, persistedEvents.Write);

            await persistence.ReadAsync();

            await persistence.WriteEventAsync(Envelope.Create(new MyEvent()));
            await persistence.WriteEventAsync(Envelope.Create(new MyEvent()));

            A.CallTo(() => eventStore.AppendAsync(A<Guid>._, key.ToString(), 2, A<ICollection<EventData>>.That.Matches(x => x.Count == 1)))
                .MustHaveHappened();
            A.CallTo(() => eventStore.AppendAsync(A<Guid>._, key.ToString(), 3, A<ICollection<EventData>>.That.Matches(x => x.Count == 1)))
                .MustHaveHappened();

            A.CallTo(() => snapshotStore.WriteAsync(A<DomainId>._, A<int>._, A<long>._, A<long>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_write_events_to_store_with_empty_version()
        {
            var persistence = sut.WithEventSourcing(None.Type, key, null);

            await persistence.WriteEventAsync(Envelope.Create(new MyEvent()));

            A.CallTo(() => eventStore.AppendAsync(A<Guid>._, key.ToString(), EtagVersion.Empty, A<ICollection<EventData>>.That.Matches(x => x.Count == 1)))
                .MustHaveHappened();
        }

        [Fact]
        public async Task Should_write_snapshot_to_store()
        {
            A.CallTo(() => snapshotStore.ReadAsync(key))
                .Returns((2, 2L));

            SetupEventStore(3);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await persistence.ReadAsync();

            await persistence.WriteEventAsync(Envelope.Create(new MyEvent()));
            await persistence.WriteSnapshotAsync(4);

            await persistence.WriteEventAsync(Envelope.Create(new MyEvent()));
            await persistence.WriteSnapshotAsync(5);

            A.CallTo(() => snapshotStore.WriteAsync(key, 4, 2, 3))
                .MustHaveHappened();
            A.CallTo(() => snapshotStore.WriteAsync(key, 5, 3, 4))
                .MustHaveHappened();
        }

        [Fact]
        public async Task Should_write_snapshot_to_store_when_not_read_before()
        {
            A.CallTo(() => snapshotStore.ReadAsync(key))
                .Returns((default, EtagVersion.Empty));

            SetupEventStore(3);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await persistence.ReadAsync();

            await persistence.WriteEventAsync(Envelope.Create(new MyEvent()));
            await persistence.WriteSnapshotAsync(4);

            await persistence.WriteEventAsync(Envelope.Create(new MyEvent()));
            await persistence.WriteSnapshotAsync(5);

            A.CallTo(() => snapshotStore.WriteAsync(key, 4, 2, 3))
                .MustHaveHappened();
            A.CallTo(() => snapshotStore.WriteAsync(key, 5, 3, 4))
                .MustHaveHappened();
        }

        [Fact]
        public async Task Should_not_write_snapshot_to_store_when_not_changed()
        {
            A.CallTo(() => snapshotStore.ReadAsync(key))
                .Returns((0, 2));

            SetupEventStore(3);

            var persistedState = Save.Snapshot(-1);
            var persistedEvents = Save.Events();
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, persistedState.Write, persistedEvents.Write);

            await persistence.ReadAsync();

            await persistence.WriteSnapshotAsync(4);

            A.CallTo(() => snapshotStore.WriteAsync(key, A<int>._, A<long>._, A<long>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_wrap_exception_when_writing_to_store_with_previous_version()
        {
            SetupEventStore(3);

            var persistedEvents = Save.Events();
            var persistence = sut.WithEventSourcing(None.Type, key, persistedEvents.Write);

            await persistence.ReadAsync();

            A.CallTo(() => eventStore.AppendAsync(A<Guid>._, key.ToString(), 2, A<ICollection<EventData>>.That.Matches(x => x.Count == 1)))
                .Throws(new WrongEventVersionException(1, 1));

            await Assert.ThrowsAsync<InconsistentStateException>(() => persistence.WriteEventAsync(Envelope.Create(new MyEvent())));
        }

        [Fact]
        public async Task Should_delete_events_but_not_snapshot_when_deleted_snapshot_only()
        {
            var persistence = sut.WithEventSourcing(None.Type, key, null);

            await persistence.DeleteAsync();

            A.CallTo(() => eventStore.DeleteStreamAsync(key.ToString()))
                .MustHaveHappened();

            A.CallTo(() => snapshotStore.RemoveAsync(key))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_delete_events_and_snapshot_when_deleted()
        {
            var persistence = sut.WithSnapshotsAndEventSourcing(None.Type, key, null, null);

            await persistence.DeleteAsync();

            A.CallTo(() => eventStore.DeleteStreamAsync(key.ToString()))
                .MustHaveHappened();

            A.CallTo(() => snapshotStore.RemoveAsync(key))
                .MustHaveHappened();
        }

        private void SetupEventStore(int count, int eventOffset = 0, int readPosition = 0)
        {
            SetupEventStore(Enumerable.Repeat(0, count).Select(x => new MyEvent()).ToArray(), eventOffset, readPosition);
        }

        private void SetupEventStore(params MyEvent[] events)
        {
            SetupEventStore(events, 0, 0);
        }

        private void SetupEventStore(MyEvent[] events, int eventOffset, int readPosition = 0)
        {
            var eventsStored = new List<StoredEvent>();

            var i = eventOffset;

            foreach (var @event in events)
            {
                var eventData = new EventData("Type", new EnvelopeHeaders(), "Payload");
                var eventStored = new StoredEvent(key.ToString(), i.ToString(), i, eventData);

                eventsStored.Add(eventStored);

                A.CallTo(() => eventDataFormatter.Parse(eventStored))
                    .Returns(new Envelope<IEvent>(@event));

                A.CallTo(() => eventDataFormatter.ParseIfKnown(eventStored))
                    .Returns(new Envelope<IEvent>(@event));

                i++;
            }

            A.CallTo(() => eventStore.QueryAsync(key.ToString(), readPosition))
                .Returns(eventsStored);
        }
    }
}